using System;
using Math = System.Math;
using System.Text;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using Mono.Debugger;
using Mono.Debugger.Languages;

namespace Mono.Debugger.Frontend
{
	/// <summary>
	///   This interface controls how things are being displayed to the
	///   user, for instance the current stack frame or variables from
	///   the target.
	/// </summary>
	public interface Style
	{
		string Name {
			get;
		}

		bool IsNative {
			get; set;
		}

		void Reset ();

		void PrintFrame (ScriptingContext context, FrameHandle frame);

		void TargetStopped (ScriptingContext context, FrameHandle frame,
				    AssemblerLine current_insn);

		void UnhandledException (ScriptingContext context, FrameHandle frame,
					 AssemblerLine current_insn, ITargetObject exc);

		void ShowVariableType (ITargetType type, string name);

		void PrintVariable (IVariable variable, StackFrame frame);

		string FormatObject (object obj);

		string FormatType (ITargetType type);
	}

	public class StyleMono : StyleNative
	{
		public StyleMono (Interpreter interpreter)
			: base (interpreter)
		{ }

		public override string Name {
			get {
				return "mono";
			}
		}

		public override void Reset ()
		{
			IsNative = false;
		}

		public override void UnhandledException (ScriptingContext context,
							 FrameHandle frame, AssemblerLine insn,
							 ITargetObject exc)
		{
			base.UnhandledException (context, frame, insn, exc);
		}
	}

	public class StyleEmacs : StyleMono
	{
		public StyleEmacs (Interpreter interpreter)
			: base (interpreter)
		{ }

		public override string Name {
			get {
				return "emacs";
			}
		}

		public override void TargetStopped (ScriptingContext context, FrameHandle frame,
						    AssemblerLine current_insn)
		{
			if (frame == null)
				return;

			StackFrame stack_frame = frame.Frame;
			if (stack_frame != null && stack_frame.SourceAddress != null)
				Console.WriteLine ("\x1A\x1A{0}:{1}:beg:{2}", stack_frame.SourceAddress.Name, "55" /* XXX */, "0x80594d8" /* XXX */);
		}
	}

	public class StyleNative : StyleBase, Style
	{
		public StyleNative (Interpreter interpreter)
			: base (interpreter)
		{ }

		bool native;

		public virtual string Name {
			get {
				return "native";
			}
		}

		public bool IsNative {
			get { return native; }
			set { native = value; }
		}

		public virtual void Reset ()
		{
			IsNative = true;
		}

		public virtual void PrintFrame (ScriptingContext context, FrameHandle frame)
		{
			context.Print (frame);
			if (!frame.PrintSource (context))
				native = true;
			if (native)
				frame.Disassemble (context);
		}

		public virtual void TargetStopped (ScriptingContext context, FrameHandle frame,
					   AssemblerLine current_insn)
		{
			if (frame != null) {
				if (!frame.PrintSource (context))
					native = true;
			}
			if (native && (current_insn != null))
				context.PrintInstruction (current_insn);
		}

		public virtual void UnhandledException (ScriptingContext context,
							FrameHandle frame, AssemblerLine insn,
							ITargetObject exc)
		{
			TargetStopped (context, frame, insn);
		}

		public string FormatObject (object obj)
		{
			if (obj is long)
				return String.Format ("0x{0:x}", (long) obj);
			else if (obj is string)
				return '"' + (string) obj + '"';
			else if (obj is ITargetType)
				return ((ITargetType) obj).Name;
			else if (obj is ITargetObject) {
				ITargetObject tobj = (ITargetObject) obj;
				return String.Format ("({0}) {1}", tobj.Type.Name,
						      FormatObject (tobj, false));
			} else
				return obj.ToString ();
		}

		protected string FormatMember (string prefix, ITargetMemberInfo member,
					       bool is_static, Hashtable hash)
		{
			string tname = member.Type.Name;
			if (is_static)
				return String.Format (
					"{0}   static {1} {2}", prefix, tname, member.Name);
			else
				return String.Format (
					"{0}   {1} {2}", prefix, tname, member.Name);
		}

		protected string FormatProperty (string prefix, ITargetPropertyInfo prop,
						 bool is_static, Hashtable hash)
		{
			StringBuilder sb = new StringBuilder ();
			sb.Append (FormatMember (prefix, prop, is_static, hash));
			sb.Append (" {");
			if (prop.CanRead)
				sb.Append (" get;");
			if (prop.CanWrite)
				sb.Append (" set;");
			sb.Append (" };\n");
			return sb.ToString ();
		}

		protected string FormatEvent (string prefix, ITargetEventInfo ev,
						 bool is_static, Hashtable hash)
		{
			string tname = ev.Type.Name;
			if (is_static)
				return String.Format (
					"{0}   static event {1} {2};\n", prefix, tname, ev.Name);
			else
				return String.Format (
					"{0}   event {1} {2};\n", prefix, tname, ev.Name);
		}

		protected string FormatMethod (string prefix, ITargetMethodInfo method,
					       bool is_static, bool is_ctor, Hashtable hash)
		{
			StringBuilder sb = new StringBuilder ();
			sb.Append (prefix);
			if (is_ctor)
				if (is_static)
					sb.Append ("   .cctor ");
				else
					sb.Append ("   .ctor ");
			else if (is_static)
				sb.Append ("   static ");
			else
				sb.Append ("   ");

			ITargetFunctionType ftype = method.Type;
			if (!is_ctor) {
				if (ftype.HasReturnValue)
					sb.Append (ftype.ReturnType.Name);
				else
					sb.Append ("void");
				sb.Append (" ");
				sb.Append (method.Name);
				sb.Append (" ");
			}
			sb.Append ("(");
			bool first = true;
			foreach (ITargetType ptype in ftype.ParameterTypes) {
				if (first)
					first = false;
				else
					sb.Append (", ");
				sb.Append (ptype.Name);
			}
			sb.Append (");\n");
			return sb.ToString ();
		}

		public string FormatType (ITargetType type)
		{
			return FormatType ("", type, null);
		}

		protected string FormatType (string prefix, ITargetType type, Hashtable hash)
		{
			string retval;

			if (hash == null)
				hash = new Hashtable ();

			if (hash.Contains (type))
				return type.Name;
			else
				hash.Add (type, true);

			switch (type.Kind) {
			case TargetObjectKind.Array: {
				ITargetArrayType atype = (ITargetArrayType) type;
				retval = String.Format (
					"{0} []", atype.ElementType.Name);
				break;
			}

			case TargetObjectKind.Class:
			case TargetObjectKind.Struct: {
				ITargetStructType stype = (ITargetStructType) type;
				StringBuilder sb = new StringBuilder ();
				ITargetClassType ctype = type as ITargetClassType;
				if (type.Kind == TargetObjectKind.Struct)
					sb.Append ("struct ");
				else
					sb.Append ("class ");
				if (ctype != null) {
					if (ctype.Name != null) {
						sb.Append (ctype.Name);
						sb.Append (" ");
					}
					if (ctype.HasParent) {
						sb.Append (": ");
						sb.Append (ctype.ParentType.Name);
					}
				} else {
					if (stype.Name != null) {
						sb.Append (stype.Name);
						sb.Append (" ");
					}
				}
				sb.Append ("\n" + prefix + "{\n");
				foreach (ITargetFieldInfo field in stype.Fields)
					sb.Append (FormatMember (
							   prefix, field, false, hash) + ";\n");
				foreach (ITargetFieldInfo field in stype.StaticFields)
					sb.Append (FormatMember (
							   prefix, field, true, hash) + ";\n");
				foreach (ITargetPropertyInfo property in stype.Properties)
					sb.Append (FormatProperty (
							   prefix, property, false, hash));
				foreach (ITargetPropertyInfo property in stype.StaticProperties)
					sb.Append (FormatProperty (
							   prefix, property, true, hash));
				foreach (ITargetEventInfo ev in stype.Events)
					sb.Append (FormatEvent (
							   prefix, ev, false, hash));
				foreach (ITargetEventInfo ev in stype.StaticEvents)
					sb.Append (FormatEvent (
							   prefix, ev, true, hash));
				foreach (ITargetMethodInfo method in stype.Methods)
					sb.Append (FormatMethod (
							   prefix, method, false, false, hash));
				foreach (ITargetMethodInfo method in stype.StaticMethods)
					sb.Append (FormatMethod (
							   prefix, method, true, false, hash));
				foreach (ITargetMethodInfo method in stype.Constructors)
					sb.Append (FormatMethod (
							   prefix, method, false, true, hash));
				foreach (ITargetMethodInfo method in stype.StaticConstructors)
					sb.Append (FormatMethod (
							   prefix, method, true, true, hash));

				sb.Append (prefix);
				sb.Append ("}");

				retval = sb.ToString ();
				break;
			}

			case TargetObjectKind.Alias: {
				ITargetTypeAlias alias = (ITargetTypeAlias) type;
				string target;
				if (alias.TargetType != null)
					target = FormatType (prefix, alias.TargetType, hash);
				else
					target = "<unknown type>";
				retval = String.Format (
					"typedef {0} = {1}", alias.Name, target);
				break;
			}

			default:
				retval = type.Name;
				break;
			}

			hash.Remove (type);
			return retval;
		}

		public string FormatObject (ITargetObject obj, bool recursed)
		{
			try {
				if (recursed)
					return DoFormatObjectRecursed (obj);
				else
					return DoFormatObject (obj);
			} catch {
				return "<cannot display object>";
			}
		}

		protected string DoFormatObjectRecursed (ITargetObject obj)
		{
			switch (obj.Type.Kind) {
			case TargetObjectKind.Class:
			case TargetObjectKind.Struct:
				return String.Format (
					"({0}) {1}", obj.Type.Name, obj.Location.Address);

			default:
				return obj.Print ();
			}
		}

		protected string DoFormatObject (ITargetObject obj)
		{
			switch (obj.Type.Kind) {
			case TargetObjectKind.Array: {
				ITargetArrayObject aobj = (ITargetArrayObject) obj;
				StringBuilder sb = new StringBuilder ("[");
				int lower = aobj.LowerBound;
				int upper = aobj.UpperBound;
				for (int i = lower; i < upper; i++) {
					if (i > lower)
						sb.Append (",");
					sb.Append (FormatObject (aobj [i], true));
				}
				sb.Append ("]");
				return sb.ToString ();
			}

			case TargetObjectKind.Pointer: {
				ITargetPointerObject pobj = (ITargetPointerObject) obj;
				if (pobj.Type.IsTypesafe && pobj.HasDereferencedObject) {
					ITargetObject deref = pobj.DereferencedObject;
					return String.Format ("&({0}) {1}", deref.Type.Name,
							      FormatObject (deref, false));
				} else
					return pobj.Print ();
			}

			case TargetObjectKind.Class:
			case TargetObjectKind.Struct: {
				ITargetStructObject sobj = (ITargetStructObject) obj;
				StringBuilder sb = new StringBuilder ("{ ");
				bool first = true;
				ITargetFieldInfo[] fields = sobj.Type.Fields;
				foreach (ITargetFieldInfo field in fields) {
					ITargetObject fobj = sobj.GetField (field.Index);
					if (first)
						first = false;
					else
						sb.Append (",  ");
					sb.Append (field.Name + "=");
					if (fobj == null)
						sb.Append ("null");
					else
						sb.Append (FormatObject (fobj, true));
				}
				sb.Append (" }");
				return sb.ToString ();
			}

			default:
				return obj.Print ();
			}
		}

		public void PrintVariable (IVariable variable, StackFrame frame)
		{
			ITargetObject obj = null;
			try {
				obj = variable.GetObject (frame);
			} catch {
			}

			string contents;
			try {
				if (obj != null)
					contents = FormatObject (obj, false);
				else
					contents = "<cannot display object>";
			} catch {
				contents = "<cannot display object>";
			}
				
			Print ("{0} = ({1}) {2}", variable.Name, variable.Type.Name,
			       contents);
		}

		public void ShowVariableType (ITargetType type, string name)
		{
			Print (type.Name);
		}
	}


	///
	/// Ignore this `user interface' - I need it to debug the debugger.
	///

	public class StyleMartin : StyleBase, Style
	{
		public StyleMartin (Interpreter interpreter)
			: base (interpreter)
		{ }

		public string Name {
			get { return "martin"; }
		}

		bool Style.IsNative {
			get { return true; }
			set { ; }
		}

		public void Reset ()
		{ }

		public void TargetStopped (ScriptingContext context, FrameHandle frame,
					   AssemblerLine current_insn)
		{
			if (current_insn != null)
				context.PrintInstruction (current_insn);

			if (frame != null)
				frame.PrintSource (context);
		}

		public void UnhandledException (ScriptingContext context,
						FrameHandle frame, AssemblerLine insn,
						ITargetObject exc)
		{
			TargetStopped (context, frame, insn);
		}

		public void PrintFrame (ScriptingContext context, FrameHandle frame)
		{
			context.Print (frame);
			frame.Disassemble (context);
			frame.PrintSource (context);
		}

		public void PrintVariable (IVariable variable, StackFrame frame)
		{
			ITargetObject obj = null;
			try {
				obj = variable.GetObject (frame);
			} catch {
			}

			string contents;
			if (obj != null)
				contents = FormatObject (obj);
			else
				contents = "<cannot display object>";
				
			Print ("{0} = ({1}) {2}", variable.Name, variable.Type.Name,
			       contents);
		}

		public string FormatObject (object obj)
		{
			if (obj is long)
				return String.Format ("0x{0:x}", (long) obj);
			else
				return obj.ToString ();
		}

		public string FormatType (ITargetType type)
		{
			return type.ToString ();
		}

		public void ShowVariableType (ITargetType type, string name)
		{
			ITargetArrayType array = type as ITargetArrayType;
			if (array != null)
				Print ("{0} is an array of {1}", name, array.ElementType);

			ITargetClassType tclass = type as ITargetClassType;
			ITargetStructType tstruct = type as ITargetStructType;
			if (tclass != null) {
				if (tclass.HasParent)
					Print ("{0} is a class of type {1} which inherits from {2}",
					       name, tclass.Name, tclass.ParentType);
				else
					Print ("{0} is a class of type {1}", name, tclass.Name);
			} else if (tstruct != null)
				Print ("{0} is a value type of type {1}", name, tstruct.Name);

			if (tstruct != null) {
				foreach (ITargetFieldInfo field in tstruct.Fields)
					Print ("  It has a field `{0}' of type {1}", field.Name,
					       field.Type.Name);
				foreach (ITargetFieldInfo field in tstruct.StaticFields)
					Print ("  It has a static field `{0}' of type {1}", field.Name,
					       field.Type.Name);
				foreach (ITargetFieldInfo property in tstruct.Properties)
					Print ("  It has a property `{0}' of type {1}", property.Name,
					       property.Type.Name);
				foreach (ITargetMethodInfo method in tstruct.Methods) {
					if (method.Type.HasReturnValue)
						Print ("  It has a method: {0} {1}", method.Type.ReturnType.Name, method.FullName);
					else
						Print ("  It has a method: void {0}", method.FullName);
				}
				foreach (ITargetMethodInfo method in tstruct.StaticMethods) {
					if (method.Type.HasReturnValue)
						Print ("  It has a static method: {0} {1}", method.Type.ReturnType.Name, method.FullName);
					else
						Print ("  It has a static method: void {0}", method.FullName);
				}
				foreach (ITargetMethodInfo method in tstruct.Constructors) {
					Print ("  It has a constructor: {0}", method.FullName);
				}
				return;
			}

			Print ("{0} is a {1}", name, type);
		}
	}

	public abstract class StyleBase
	{
		Interpreter interpreter;

		protected StyleBase (Interpreter interpreter)
		{
			this.interpreter = interpreter;
		}

		public void Print (string message)
		{
			interpreter.Print (message);
		}

		public void Print (string format, params object[] args)
		{
			interpreter.Print (String.Format (format, args));
		}

		public void Print (object obj)
		{
			interpreter.Print (obj.ToString ());
		}
	}
}