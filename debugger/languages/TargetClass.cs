using System;

namespace Mono.Debugger.Languages
{
	public abstract class TargetClass : DebuggerMarshalByRefObject
	{
		public abstract TargetStructType Type {
			get;
		}

		public abstract TargetType RealType {
			get;
		}

		public abstract bool HasParent {
			get;
		}

		public abstract TargetClass GetParent (Thread thread);

		public abstract TargetFieldInfo[] GetFields (Thread thread);

		public abstract TargetObject GetField (Thread thread,
						       TargetStructObject instance,
						       TargetFieldInfo field);

		public abstract void SetField (Thread thread, TargetStructObject instance,
					       TargetFieldInfo field, TargetObject value);

		public abstract TargetMethodInfo[] GetMethods (Thread thread);

		public virtual TargetMemberInfo FindMember (Thread thread, string name,
							    bool search_static, bool search_instance)
		{
			foreach (TargetFieldInfo field in GetFields (thread)) {
				if (field.IsStatic && !search_static)
					continue;
				if (!field.IsStatic && !search_instance)
					continue;
				if (field.Name == name)
					return field;
			}

			return null;
		}
	}
}