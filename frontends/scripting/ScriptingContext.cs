using System;
using System.Text;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using Mono.Debugger;
using Mono.Debugger.Backends;

using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

using Mono.GetOptions;

namespace Mono.Debugger.Frontends.CommandLine
{
	public delegate void ProcessExitedHandler (ProcessHandle handle);

	public class FrameHandle
	{
		ProcessHandle process;
		StackFrame frame;

		public FrameHandle (ProcessHandle process, StackFrame frame)
		{
			this.process = process;
			this.frame = frame;
		}

		public StackFrame Frame {
			get { return frame; }
		}

		public void Print (ScriptingContext context)
		{
			context.Print (frame);
			Disassemble (context);
			PrintSource (context);
		}

		public void PrintSource (ScriptingContext context)
		{
			SourceAddress location = frame.SourceAddress;
			if (location == null)
				return;

			IMethod method = frame.Method;
			if ((method == null) || !method.HasSource || (method.Source == null))
				return;

			IMethodSource source = method.Source;
			if (source.SourceBuffer == null)
				return;

			string line = source.SourceBuffer.Contents [location.Row - 1];
			context.Print (String.Format ("{0,4} {1}", location.Row, line));
		}

		public void Disassemble (ScriptingContext context)
		{
			Disassemble (context, frame.TargetAddress);
		}

		AssemblerLine Disassemble (ScriptingContext context, TargetAddress address)
		{
			AssemblerLine line = frame.DisassembleInstruction (address);

			if (line != null)
				context.PrintInstruction (line);
			else
				context.Error ("Cannot disassemble instruction at address {0}.", address);

			return line;
		}

		public void DisassembleMethod (ScriptingContext context)
		{
			IMethod method = frame.Method;

			if ((method == null) || !method.IsLoaded)
				throw new ScriptingException ("Selected stack frame has no method.");

			AssemblerMethod asm = frame.DisassembleMethod ();
			foreach (AssemblerLine line in asm.Lines)
				context.PrintInstruction (line);
		}

		public int[] RegisterIndices {
			get { return process.Architecture.RegisterIndices; }
		}

		public string[] RegisterNames {
			get { return process.Architecture.RegisterNames; }
		}

		public Register[] GetRegisters (int[] indices)
		{
			Register[] registers = frame.Registers;
			if (registers == null)
				throw new ScriptingException ("Cannot get registers of selected stack frame.");

			if (indices == null)
				return registers;

			ArrayList list = new ArrayList ();
			for (int i = 0; i < indices.Length; i++) {
				foreach (Register register in registers) {
					if (register.Index == indices [i]) {
						list.Add (register);
						break;
					}
				}
			}

			Register[] retval = new Register [list.Count];
			list.CopyTo (retval, 0);
			return retval;	
		}

		public ITargetObject GetRegister (string name, long offset)
		{
			int register = process.GetRegisterIndex (name);

			return frame.GetRegister (register, offset);
		}

		public void SetRegister (string name, long value)
		{
			int register = process.GetRegisterIndex (name);

			frame.SetRegister (register, value);
		}

		public void ShowParameters (ScriptingContext context)
		{
			if (frame.Method == null)
				throw new ScriptingException ("Selected stack frame has no method.");

			IVariable[] param_vars = frame.Method.Parameters;
			foreach (IVariable var in param_vars)
				PrintVariable (context, true, var);
		}

		public void ShowLocals (ScriptingContext context)
		{
			if (frame.Method == null)
				throw new ScriptingException ("Selected stack frame has no method.");

			IVariable[] local_vars = frame.Locals;
			foreach (IVariable var in local_vars)
				PrintVariable (context, false, var);
		}

		public void PrintVariable (ScriptingContext context, bool is_param, IVariable variable)
		{
			context.Print (variable);
		}

		public IVariable GetVariableInfo (string identifier)
		{
			if (frame.Method == null)
				throw new ScriptingException ("Selected stack frame has no method.");

			IVariable[] local_vars = frame.Locals;
			foreach (IVariable var in local_vars) {
				if (var.Name == identifier)
					return var;
			}

			IVariable[] param_vars = frame.Method.Parameters;
			foreach (IVariable var in param_vars) {
				if (var.Name == identifier)
					return var;
			}

			throw new ScriptingException ("No variable or parameter with that name: `{0}'.", identifier);
		}

		public ITargetObject GetVariable (string identifier)
		{
			IVariable var = GetVariableInfo (identifier);
			if (!var.IsAlive (frame.TargetAddress))
				throw new ScriptingException ("Variable out of scope.");
			if (!var.CheckValid (frame))
				throw new ScriptingException ("Variable cannot be accessed.");

			return var.GetObject (frame);
		}

		public ITargetType GetVariableType (string identifier)
		{
			IVariable var = GetVariableInfo (identifier);
			if (!var.IsAlive (frame.TargetAddress))
				throw new ScriptingException ("Variable out of scope.");
			if (!var.CheckValid (frame))
				throw new ScriptingException ("Variable cannot be accessed.");

			return var.Type;
		}

		public ITargetType GetRegisterType (string register)
		{
			return frame.Language.LongIntegerType;
		}

		public override string ToString ()
		{
			return frame.ToString ();
		}
	}

	public class BacktraceHandle
	{
		ProcessHandle process;
		Backtrace backtrace;
		FrameHandle[] frames;

		public BacktraceHandle (ProcessHandle process, Backtrace backtrace)
		{
			this.process = process;
			this.backtrace = backtrace;

			StackFrame[] bt_frames = backtrace.Frames;
			if (bt_frames != null) {
				frames = new FrameHandle [bt_frames.Length];
				for (int i = 0; i < frames.Length; i++)
					frames [i] = new FrameHandle (process, bt_frames [i]);
			} else
				frames = new FrameHandle [0];
		}

		public int Length {
			get { return frames.Length; }
		}

		public FrameHandle this [int number] {
			get { return frames [number]; }
		}
	}

	public class ProcessHandle
	{
		DebuggerBackend backend;
		ScriptingContext context;	
		IArchitecture arch;
		Process process;
		string name;

		int id, pid;
		Hashtable registers;

		public ProcessHandle (ScriptingContext context, DebuggerBackend backend, Process process)
		{
			this.context = context;
			this.backend = backend;
			this.process = process;
			this.id = process.ID;

			if (!process.IsDaemon)
				process.TargetEvent += new TargetEventHandler (target_event);
			process.TargetExited += new TargetExitedHandler (target_exited);
			process.DebuggerOutput += new DebuggerOutputHandler (debugger_output);
			process.DebuggerError += new DebuggerErrorHandler (debugger_error);

			if (process.IsDaemon)
				name = String.Format ("Daemon process @{0}", id);
			else
				name = String.Format ("Process @{0}", id);
		}

		public ProcessHandle (ScriptingContext context, DebuggerBackend backend, Process process,
				      int pid)
			: this (context, backend, process)
		{
			if (process.SingleSteppingEngine == null)
				return;

			if (process.SingleSteppingEngine.HasTarget) {
				if (!process.IsDaemon) {
					StackFrame frame = process.SingleSteppingEngine.CurrentFrame;
					current_frame = new FrameHandle (this, frame);
					context.Print ("{0} stopped at {1}.", Name, frame);
					current_frame.Print (context);
				}
			} else {
				if (pid > 0)
					process.SingleSteppingEngine.Attach (pid, true);
				else
					process.SingleSteppingEngine.Run (!context.IsSynchronous, true);
			}
			initialize ();
		}

		public ProcessHandle (ScriptingContext context, DebuggerBackend backend, Process process,
				      string core_file)
			: this (context, backend, process)
		{
			initialize ();
		}

		public Process Process {
			get { return process; }
		}

		public event ProcessExitedHandler ProcessExitedEvent;

		void initialize ()
		{
			registers = new Hashtable ();
			arch = process.Architecture;

			for (int i = 0; i < arch.RegisterNames.Length; i++) {
				string register = arch.RegisterNames [i];

				registers.Add (register, arch.AllRegisterIndices [i]);
			}
		}

		int current_frame_idx = -1;
		FrameHandle current_frame = null;
		BacktraceHandle current_backtrace = null;
		AssemblerLine current_insn = null;

		void target_exited ()
		{
			process = null;

			if (ProcessExitedEvent != null)
				ProcessExitedEvent (this);
		}

		void target_event (object sender, TargetEventArgs args)
		{
			if (args.Frame != null) {
				current_frame = new FrameHandle (this, args.Frame);
				current_frame_idx = -1;
				current_backtrace = null;

				current_insn = args.Frame.DisassembleInstruction (args.Frame.TargetAddress);
			} else {
				current_insn = null;
				current_frame = null;
				current_frame_idx = -1;
				current_backtrace = null;
			}

			string frame = "";
			if (current_frame != null)
				frame = String.Format (" at {0}", current_frame);

			switch (args.Type) {
			case TargetEventType.TargetStopped:
				if ((int) args.Data != 0)
					context.Print ("{0} received signal {1}{2}.", Name, (int) args.Data, frame);
				else if (!context.IsInteractive)
					break;
				else
					context.Print ("{0} stopped{1}.", Name, frame);

				if (current_insn != null)
					context.PrintInstruction (current_insn);

				if (current_frame != null)
					current_frame.PrintSource (context);

				if (!context.IsInteractive)
					context.Abort ();
				break;

			case TargetEventType.TargetExited:
				if ((int) args.Data == 0)
					context.Print ("{0} terminated normally.", Name);
				else
					context.Print ("{0} exited with exit code {1}.", id, (int) args.Data);
				target_exited ();
				break;

			case TargetEventType.TargetSignaled:
				context.Print ("{0} died with fatal signal {1}.",
					       id, (int) args.Data);
				target_exited ();
				break;
			}
		}

		void debugger_output (string line)
		{
			context.Print ("DEBUGGER OUTPUT: {0}", line);
		}

		void debugger_error (object sender, string message, Exception e)
		{
			context.Print ("DEBUGGER ERROR: {0}\n{1}", message, e);
		}

		public void Step (WhichStepCommand which)
		{
			if (process == null)
				throw new ScriptingException ("{0} not running.", Name);
			else if (!process.CanRun)
				throw new ScriptingException ("{0} cannot be executed.", Name);

			bool ok;
			switch (which) {
			case WhichStepCommand.Continue:
				ok = process.Continue (context.IsSynchronous);
				break;
			case WhichStepCommand.Step:
				ok = process.StepLine (context.IsSynchronous);
				break;
			case WhichStepCommand.Next:
				ok = process.NextLine (context.IsSynchronous);
				break;
			case WhichStepCommand.StepInstruction:
				ok = process.StepInstruction (context.IsSynchronous);
				break;
			case WhichStepCommand.StepNativeInstruction:
				ok = process.StepNativeInstruction (context.IsSynchronous);
				break;
			case WhichStepCommand.NextInstruction:
				ok = process.NextInstruction (context.IsSynchronous);
				break;
			case WhichStepCommand.Finish:
				ok = process.Finish (context.IsSynchronous);
				break;
			default:
				throw new Exception ();
			}

			if (!ok)
				throw new ScriptingException ("{0} is not stopped.", Name);
		}

		public void Stop ()
		{
			if (process == null)
				throw new ScriptingException ("{0} not running.", Name);
			process.Stop ();
		}

		public void Background ()
		{
			if (process == null)
				throw new ScriptingException ("{0} not running.", Name);
			else if (!process.CanRun)
				throw new ScriptingException ("{0} cannot be executed.", Name);
			else if (!process.IsStopped)
				throw new ScriptingException ("{0} is not stopped.", Name);

			process.Continue (true, false);
		}

		public IArchitecture Architecture {
			get {
				if (arch == null)
					throw new ScriptingException ("{0} not running.", Name);

				return arch;
			}
		}

		public int ID {
			get {
				return id;
			}
		}

		public int CurrentFrameIndex {
			get {
				if (current_frame_idx == -1)
					return 0;

				return current_frame_idx;
			}

			set {
				GetBacktrace (-1);
				if ((value < 0) || (value >= current_backtrace.Length))
					throw new ScriptingException ("No such frame.");

				current_frame_idx = value;
				current_frame = current_backtrace [current_frame_idx];
			}
		}

		public BacktraceHandle GetBacktrace (int max_frames)
		{
			if (State == TargetState.NO_TARGET)
				throw new ScriptingException ("No stack.");
			else if (!process.IsStopped)
				throw new ScriptingException ("{0} is not stopped.", Name);

			if ((max_frames == -1) && (current_backtrace != null))
				return current_backtrace;

			current_backtrace = new BacktraceHandle (this, process.GetBacktrace (max_frames));

			if (current_backtrace == null)
				throw new ScriptingException ("No stack.");

			return current_backtrace;
		}

		public FrameHandle CurrentFrame {
			get {
				return GetFrame (current_frame_idx);
			}
		}

		public FrameHandle GetFrame (int number)
		{
			if (State == TargetState.NO_TARGET)
				throw new ScriptingException ("No stack.");
			else if (!process.IsStopped)
				throw new ScriptingException ("{0} is not stopped.", Name);

			if (number == -1) {
				if (current_frame == null)
					current_frame = new FrameHandle (this, process.CurrentFrame);

				return current_frame;
			}

			GetBacktrace (-1);
			if (number >= current_backtrace.Length)
				throw new ScriptingException ("No such frame: {0}", number);

			return current_backtrace [number];
		}

		public TargetState State {
			get {
				if (process == null)
					return TargetState.NO_TARGET;
				else
					return process.State;
			}
		}

		public int GetRegisterIndex (string name)
		{
			if (!registers.Contains (name))
				throw new ScriptingException ("No such register: %{0}", name);

			return (int) registers [name];
		}
		
		public void Kill ()
		{
			process.Kill ();
			process = null;
			target_exited ();
		}

		public string Name {
			get {
				return name;
			}
		}

		public override string ToString ()
		{
			if (process.IsDaemon)
				return String.Format ("Daemon process @{0}: {1} {2}", id, process.PID, State);
			else
				return String.Format ("Process @{0}: {1} {2}", id, process.PID, State);
		}
	}

	public class ScriptingException : Exception
	{
		public ScriptingException (string format, params object[] args)
			: base (String.Format (format, args))
		{ }
	}

	public enum ModuleOperation
	{
		Ignore,
		UnIgnore,
		Step,
		DontStep,
		ShowBreakpoints
	}

	public enum WhichStepCommand
	{
		Continue,
		Step,
		Next,
		StepInstruction,
		StepNativeInstruction,
		NextInstruction,
		Finish
	}

	public class ScriptingContext
	{
		ProcessHandle current_process;
		ArrayList procs;

		DebuggerBackend backend;
		DebuggerTextWriter command_output;
		DebuggerTextWriter inferior_output;
		ProcessStart start;
		string prompt = "$";
		bool is_synchronous;
		bool is_interactive;
		int exit_code = 0;
		internal static readonly string DirectorySeparatorStr;
		static readonly Generator generator;

		ArrayList method_search_results;
		Hashtable scripting_variables;
		ITargetObject last_object;
		Module[] modules;

		static ScriptingContext ()
		{
			// FIXME: Why isn't this public in System.IO.Path ?
			DirectorySeparatorStr = Path.DirectorySeparatorChar.ToString ();

			generator = new Generator (Assembly.GetExecutingAssembly ());
		}

		public ScriptingContext (DebuggerTextWriter command_out, DebuggerTextWriter inferior_out,
					 bool is_synchronous, bool is_interactive, string[] args)
		{
			this.command_output = command_out;
			this.inferior_output = inferior_out;
			this.is_synchronous = is_synchronous;
			this.is_interactive = is_interactive;

			procs = new ArrayList ();
			current_process = null;

			scripting_variables = new Hashtable ();
			method_search_results = new ArrayList ();

			start = ProcessStart.Create (null, args);
			if (start != null)
				Initialize ();
		}

		public DebuggerBackend Initialize ()
		{
			if (backend != null)
				return backend;
			if (start == null)
				throw new ScriptingException ("No program loaded.");

			backend = start.Run ();
			backend.ThreadManager.ThreadCreatedEvent += new ThreadEventHandler (thread_created);
			backend.ThreadManager.TargetOutputEvent += new TargetOutputHandler (target_output);
			backend.ModulesChangedEvent += new ModulesChangedHandler (modules_changed);
			backend.ThreadManager.TargetExitedEvent += new TargetExitedHandler (target_exited);
			return backend;
		}

		public ProcessStart ProcessStart {
			get { return start; }
		}

		public DebuggerBackend DebuggerBackend {
			get {
				if (backend != null)
					return backend;

				throw new ScriptingException ("No backend loaded.");
			}
		}

		public bool IsSynchronous {
			get { return is_synchronous; }
		}

		public bool IsInteractive {
			get { return is_interactive; }
		}

		public int ExitCode {
			get { return exit_code; }
			set { exit_code = value; }
		}

		public ProcessHandle[] Processes {
			get {
				ProcessHandle[] retval = new ProcessHandle [procs.Count];
				procs.CopyTo (retval, 0);
				return retval;
			}
		}

		void target_output (bool is_stderr, string line)
		{
			PrintInferior (is_stderr, line);
		}

		public void Abort ()
		{
			Print ("Caught fatal error while running non-interactively; exiting!");
			Environment.Exit (-1);
		}

		public void Error (string message)
		{
			command_output.WriteLine (true, message);
			if (!IsInteractive)
				Abort ();
		}

		public void Error (string format, params object[] args)
		{
			Error (String.Format (format, args));
		}

		public void Error (ScriptingException ex)
		{
			Error (ex.Message);
		}

		public void Print (string format, params object[] args)
		{
			string message = String.Format (format, args);

			command_output.WriteLine (false, message);
		}

		public void Print (object obj)
		{
			Print ("{0}", obj);
		}

		public void PrintObject (object obj)
		{
			if (obj is long)
				Print (String.Format ("0x{0:x}", (long) obj));
			else if (obj is ITargetObject) {
				LastObject = (ITargetObject) obj;
				Print (LastObject.Print ());
			} else
				Print (obj);
		}

		public void PrintInstruction (AssemblerLine line)
		{
			if (line.Label != null)
				Print ("{0}:", line.Label);
			Print ("{0:11x}\t{1}", line.Address, line.Text);
		}

		public void PrintInferior (bool is_stderr, string line)
		{
			inferior_output.Write (is_stderr, line);
		}

		public ProcessHandle CurrentProcess {
			get {
				if (current_process == null)
					throw new ScriptingException ("No target.");

				return current_process;
			}

			set {
				current_process = value;
			}
		}

		public bool HasBackend {
			get {
				return backend != null;
			}
		}

		public bool HasTarget {
			get {
				return current_process != null;
			}
		}

		public string Prompt {
			get {
				return prompt;
			}
		}

		public ProcessStart Start (DebuggerOptions options, string[] args)
		{
			if (backend != null)
				throw new ScriptingException ("Already have a target.");

			start = ProcessStart.Create (options, args);

			return start;
		}

		public Process Run ()
		{
			if (current_process != null)
				throw new ScriptingException ("Process already started.");
			if (backend == null)
				throw new ScriptingException ("No program loaded.");

			Process process = backend.Run (start);
			current_process = new ProcessHandle (this, backend, process, -1);

			add_process (current_process);

			return process;
		}

		public string GetFullPath (string filename)
		{
			if (start == null)
				return Path.GetFullPath (filename);

			if (Path.IsPathRooted (filename))
				return filename;

			return String.Concat (start.BaseDirectory, DirectorySeparatorStr, filename);
		}

		void thread_created (ThreadManager manager, Process process)
		{
			ProcessHandle handle = new ProcessHandle (this, process.DebuggerBackend, process);
			add_process (handle);

			if (!process.IsDaemon)
				handle.Background ();
		}

		public void ShowVariableType (ITargetType type, string name)
		{
			ITargetArrayType array = type as ITargetArrayType;
			if (array != null) {
				Print ("{0} is an array of {1}", name, array.ElementType);
				return;
			}

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
				foreach (ITargetFieldInfo property in tstruct.Properties)
					Print ("  It has a property `{0}' of type {1}", property.Name,
					       property.Type.Name);
				foreach (ITargetMethodInfo method in tstruct.Methods)
					Print ("  It has a method: {0}", method);
				return;
			}

			Print ("{0} is a {1}", name, type);
		}

		public VariableExpression this [string identifier] {
			get {
				return (VariableExpression) scripting_variables [identifier];
			}

			set {
				if (scripting_variables.Contains (identifier))
					scripting_variables [identifier] = value;
				else
					scripting_variables.Add (identifier, value);
			}
		}

		public ITargetObject LastObject {
			get {
				return last_object;
			}

			set {
				last_object = value;
			}
		}

		void modules_changed ()
		{
			modules = backend.Modules;
		}

		public void ShowBreakpoints (Module module)
		{
			BreakpointHandle[] breakpoints = module.BreakpointHandles;
			if (breakpoints.Length == 0)
				return;

			Print ("Breakpoints for module {0}:", module.Name);
			foreach (BreakpointHandle handle in breakpoints) {
				Print ("{0} ({1}): {2}", handle.Breakpoint.Index,
				       handle.ThreadGroup.Name, handle.Breakpoint);
			}
		}

		public void ShowBreakpoints ()
		{
			if (modules == null) {
				Print ("No modules.");
				return;
			}

			foreach (Module module in modules)
				ShowBreakpoints (module);
		}

		public Breakpoint FindBreakpoint (int index, out Module out_module)
		{
			if (modules == null)
				goto error;

			foreach (Module module in modules) {
				foreach (Breakpoint breakpoint in module.Breakpoints) {
					if (breakpoint.Index == index) {
						out_module = module;
						return breakpoint;
					}
				}
			}

		error:
			out_module = null;
			throw new ScriptingException ("No such breakpoint.");
		}

		public Breakpoint GetBreakpoint (int index)
		{
			Module module;
			return FindBreakpoint (index, out module);
		}

		public void DeleteBreakpoint (Breakpoint breakpoint)
		{
			Module module;
			FindBreakpoint (breakpoint.Index, out module);
			module.RemoveBreakpoint (breakpoint.Index);
		}

		public Module[] GetModules (int[] indices)
		{
			if (modules == null)
				throw new ScriptingException ("No modules.");

			backend.ModuleManager.Lock ();

			int pos = 0;
			Module[] retval = new Module [indices.Length];

			foreach (int index in indices) {
				if ((index < 0) || (index > modules.Length))
					throw new ScriptingException ("No such module {0}.", index);

				retval [pos++] = modules [index];
			}

			backend.ModuleManager.UnLock ();

			return retval;
		}

		public SourceFile[] GetSources (int[] indices)
		{
			if (modules == null)
				throw new ScriptingException ("No modules.");

			Hashtable source_hash = new Hashtable ();

			backend.ModuleManager.Lock ();

			foreach (Module module in modules) {
				if (!module.SymbolsLoaded)
					continue;

				foreach (SourceFile source in module.Sources)
					source_hash.Add (source.ID, source);
			}

			int pos = 0;
			SourceFile[] retval = new SourceFile [indices.Length];

			foreach (int index in indices) {
				SourceFile source = (SourceFile) source_hash [index];
				if (source == null)
					throw new ScriptingException ("No such source file: {0}", index);

				retval [pos++] = source;
			}

			backend.ModuleManager.UnLock ();

			return retval;
		}

		public void ShowModules ()
		{
			if (modules == null) {
				Print ("No modules.");
				return;
			}

			for (int i = 0; i < modules.Length; i++) {
				Module module = modules [i];

				if (!module.HasDebuggingInfo)
					continue;

				Print ("{0,4} {1}{2}{3}{4}{5}", i, module.Name,
				       module.IsLoaded ? " loaded" : "",
				       module.SymbolsLoaded ? " symbols" : "",
				       module.StepInto ? " step" : "",
				       module.LoadSymbols ? "" :  " ignore");
			}
		}

		void module_operation (Module module, ModuleOperation[] operations)
		{
			foreach (ModuleOperation operation in operations) {
				switch (operation) {
				case ModuleOperation.Ignore:
					module.LoadSymbols = false;
					break;
				case ModuleOperation.UnIgnore:
					module.LoadSymbols = true;
					break;
				case ModuleOperation.Step:
					module.StepInto = true;
					break;
				case ModuleOperation.DontStep:
					module.StepInto = false;
					break;
				case ModuleOperation.ShowBreakpoints:
					ShowBreakpoints (module);
					break;
				default:
					throw new InternalError ();
				}
			}
		}

		public void ModuleOperations (Module[] modules, ModuleOperation[] operations)
		{
			backend.ModuleManager.Lock ();

			foreach (Module module in modules)
				module_operation (module, operations);

			backend.ModuleManager.UnLock ();
			backend.SymbolTableManager.Wait ();
		}

		public void ShowSources (Module module)
		{
			if (!module.SymbolsLoaded)
				return;

			Print ("Sources for module {0}:", module.Name);

			foreach (SourceFile source in module.Sources)
				Print ("  {0}", source);
		}

		public void ShowMethods (SourceFile source)
		{
			Print ("Methods from {0}:", source);
			foreach (SourceMethod method in source.Methods)
				Print ("  {0}", method);
		}

		void process_exited (ProcessHandle process)
		{
			procs.Remove (process);
			if (process == current_process)
				current_process = null;
		}

		void add_process (ProcessHandle process)
		{
			process.ProcessExitedEvent += new ProcessExitedHandler (process_exited);
			procs.Add (process);
		}

		void target_exited ()
		{
			if (backend != null)
				backend.Dispose ();
			backend = null;
		}

		public void Save (string filename)
		{
			StreamingContext context = new StreamingContext (StreamingContextStates.All, this);
			BinaryFormatter formatter = new BinaryFormatter (null, context);

			using (FileStream stream = new FileStream (filename, FileMode.Create)) {
				formatter.Serialize (stream, backend);
			}
		}

		public ProcessHandle GetProcess (int number)
		{
			if (number == -1)
				return CurrentProcess;

			foreach (ProcessHandle proc in Processes)
				if (proc.ID == number)
					return proc;

			throw new ScriptingException ("No such process: {0}", number);
		}

		public ProcessHandle[] GetProcesses (int[] indices)
		{
			ProcessHandle[] retval = new ProcessHandle [indices.Length];

			for (int i = 0; i < indices.Length; i++)
				retval [i] = GetProcess (indices [i]);

			return retval;
		}

		public void ShowThreadGroups ()
		{
			foreach (ThreadGroup group in ThreadGroup.ThreadGroups) {
				StringBuilder ids = new StringBuilder ();
				foreach (IProcess thread in group.Threads) {
					ids.Append (" @");
					ids.Append (thread.ID);
				}
				Print ("{0}:{1}", group.Name, ids.ToString ());
			}
		}

		public void CreateThreadGroup (string name)
		{
			if (ThreadGroup.ThreadGroupExists (name))
				throw new ScriptingException ("A thread group with that name already exists.");

			ThreadGroup.CreateThreadGroup (name);
		}

		public ThreadGroup GetThreadGroup (string name, bool writable)
		{
			if (!ThreadGroup.ThreadGroupExists (name))
				throw new ScriptingException ("No such thread group.");

			ThreadGroup group = ThreadGroup.CreateThreadGroup (name);

			if (writable && group.IsSystem)
				throw new ScriptingException ("Cannot modify system-created thread group.");

			return group;
		}

		public void AddToThreadGroup (string name, ProcessHandle[] threads)
		{
			ThreadGroup group = GetThreadGroup (name, true);

			foreach (ProcessHandle process in threads)
				group.AddThread (process.Process);
		}

		public void RemoveFromThreadGroup (string name, ProcessHandle[] threads)
		{
			ThreadGroup group = GetThreadGroup (name, true);
	
			foreach (ProcessHandle process in threads)
				group.RemoveThread (process.Process);
		}

		public int InsertBreakpoint (ThreadGroup group, SourceLocation location)
		{
			Breakpoint breakpoint = new SimpleBreakpoint (location.Name);
			int index = backend.InsertBreakpoint (breakpoint, group, location);
			if (index < 0)
				throw new ScriptingException ("Could not insert breakpoint.");
			return index;
		}

		public SourceLocation FindLocation (string file, int line)
		{
			string path = GetFullPath (file);
			SourceLocation location = backend.FindLocation (path, line);

			if (location != null)
				return location;
			else
				throw new ScriptingException ("No method contains the specified file/line.");
		}

		public SourceLocation FindLocation (string name)
		{
			SourceLocation location = backend.FindLocation (name);

			if (location != null)
				return location;
			else
				throw new ScriptingException ("No such method.");
		}

		public void AddMethodSearchResult (SourceMethod[] methods)
		{
			Print ("More than one method matches your query:");
			foreach (SourceMethod method in methods) {
				Print ("{0,4}  {1}", method_search_results.Count + 1, method.Name);
				method_search_results.Add (method);
			}
		}

		public SourceMethod GetMethodSearchResult (int index)
		{
			if ((index < 1) || (index > method_search_results.Count))
				throw new ScriptingException ("No such history item.");

			return (SourceMethod) method_search_results [index - 1];
		}

		int last_line = -1;
		string[] current_source_code = null;

		public void ListSourceCode (SourceLocation location)
		{
			int start;
			if (location == null) {
				if (current_source_code == null)
					return;

				start = last_line;
			} else {
				string filename = location.SourceFile.FileName;
				ISourceBuffer buffer = backend.SourceFileFactory.FindFile (filename);
				if (buffer == null)
					throw new ScriptingException (
						"Cannot find source file `{0}'", filename);

				current_source_code = buffer.Contents;
				start = Math.Max (location.Line - 2, 0);
			}

			last_line = Math.Min (start + 5, current_source_code.Length);

			for (int line = start; line < last_line; line++)
				Print (String.Format ("{0,4} {1}", line, current_source_code [line]));
		}

		public void PrintHelp (string arguments)
		{
			generator.PrintHelp (this, arguments);
		}

		public void Kill ()
		{
			if (backend != null)
				backend.ThreadManager.Kill ();
		}
	}
}

