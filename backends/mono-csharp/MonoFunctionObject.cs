using System;
using Mono.CSharp.Debugger;
using Mono.Debugger.Backends;

namespace Mono.Debugger.Languages.CSharp
{
	internal class MonoFunctionObject : MonoObject, ITargetFunctionObject
	{
		new MonoFunctionType type;

		public MonoFunctionObject (MonoFunctionType type, TargetLocation location)
			: base (type, location)
		{
			this.type = type;
		}

		ITargetFunctionType ITargetFunctionObject.Type {
			get {
				return type;
			}
		}

		protected override long GetDynamicSize (ITargetMemoryReader reader,
							TargetLocation location,
							out TargetLocation dynamic_location)
		{
			throw new InvalidOperationException ();
		}

		public ITargetObject Invoke ()
		{
			return type.Invoke (location, new ITargetObject [0]);
		}
	}
}

