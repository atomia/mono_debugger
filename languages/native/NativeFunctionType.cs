using System;
using System.Runtime.Serialization;

namespace Mono.Debugger.Languages.Native
{
	internal class NativeFunctionType : TargetFunctionType
	{
		string name;
		TargetType return_type;
		TargetType[] parameter_types;

		public NativeFunctionType (Language language, string name,
					   TargetType return_type, TargetType[] parameter_types)
			: base (language)
		{
			this.name = name;
			this.return_type = return_type;
			this.parameter_types = parameter_types;
		}

		public override string Name {
			get { return name; }
		}

		public override int Size {
			get { return Language.TargetInfo.TargetAddressSize; }
		}

		public override bool HasFixedSize {
			get { return true; }
		}

		public override bool IsByRef {
			get {
				return false;
			}
		}

		public override bool HasReturnValue {
			get {
				return return_type != Language.VoidType;
			}
		}

		public override TargetType ReturnType {
			get {
				return return_type;
			}
		}

		public override TargetType[] ParameterTypes {
			get {
				return parameter_types;
			}
		}

		public override SourceMethod Source {
			get {
				return null;
			}
		}

		public override object MethodHandle {
			get {
				return null;
			}
		}

		public override TargetClassType DeclaringType {
			get {
				return null;
			}
		}

	        internal override TargetObject GetObject (TargetLocation location)
		{
			throw new NotSupportedException ();
		}

		public override bool IsLoaded {
			get { return true; }
		}

		public override TargetAddress GetMethodAddress (Thread target)
		{
			throw new NotSupportedException ();
		}

		//
		// Session handling.
		//

		protected override void GetSessionData (SerializationInfo info)
		{
			throw new NotSupportedException ();
		}

		protected override object SetSessionData (SerializationInfo info)
		{
			throw new NotSupportedException ();
		}
	}
}
