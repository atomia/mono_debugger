using System;
using System.IO;

using Mono.Debugger.Languages;

namespace Mono.Debugger
{
	public interface IMethod
	{
		string Name {
			get;
		}

		string ImageFile {
			get;
		}

		object MethodHandle {
			get;
		}

		Module Module {
			get;
		}

		// <summary>
		//   StartAddress and EndAddress are only valid if this is true.
		// </summary>
		bool IsLoaded {
			get;
		}

		TargetAddress StartAddress {
			get;
		}

		TargetAddress EndAddress {
			get;
		}

		// <summary>
		//   MethodStartAddress and MethodEndAddress are only valid if this is true.
		// </summary>
		bool HasMethodBounds {
			get;
		}

		// <summary>
		//   This is the address of the actual start of the method's code, ie. just after
		//   the prologue.
		// </summary>
		TargetAddress MethodStartAddress {
			get;
		}

		// <summary>
		//   This is the address of the actual end of the method's code, ie. just before
		//   the epilogue.
		// </summary>
		TargetAddress MethodEndAddress {
			get;
		}

		// <summary>
		//   Whether this is an icall/pinvoke wrapper.
		//   WrapperAddress is only valid if this is true.
		// </summary>
		bool IsWrapper {
			get;
		}

		// <summary>
		//   If IsWrapper is true, this is the wrapped method's code.
		// </summary>
		TargetAddress WrapperAddress {
			get;
		}

		// <summary>
		//   Source is only valid if this is true.
		// </summary>
		bool HasSource {
			get;
		}

		// <remarks>
		//   This may return null if the source file could not be found.
		//
		// Note:
		//   The return value of this property is internally cached inside
		//   a weak reference, so it's highly recommended that you call this
		//   property multiple times instead of keeping a reference yourself.
		// </remarks>
		MethodSource Source {
			get;
		}

		// <summary>
		//   The method's parameters.
		// </summary>
		IVariable[] Parameters {
			get;
		}

		// <summary>
		//   The method's local variables
		// </summary>
		IVariable[] Locals {
			get;
		}

		SourceMethod GetTrampoline (TargetAddress address);
	}
}
