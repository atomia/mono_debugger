using System;

namespace Mono.Debugger.Languages.CSharp
{
	internal class MonoEnumType : MonoType
	{
		MonoType element_type;

		public MonoEnumType (Type type, int size, ITargetMemoryReader info)
			: base (type, size, true, info)
		{
			TargetAddress element_type_info = info.ReadAddress ();
			element_type = GetType (type.GetElementType (), info.TargetMemoryAccess,
						element_type_info);
		}

		public static bool Supports (Type type, TargetBinaryReader info)
		{
			return type.IsEnum;
		}

		public override int Size {
			get {
				return element_type.Size;
			}
		}

		public override bool IsByRef {
			get {
				return false;
			}
		}

		public override bool HasObject {
			get {
				return true;
			}
		}

		public override MonoObject GetObject (ITargetLocation location)
		{
			return new MonoEnumObject (this, location, element_type.GetObject (location));
		}
	}
}
