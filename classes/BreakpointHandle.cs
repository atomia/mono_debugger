using System;
using System.Runtime.Serialization;

namespace Mono.Debugger
{
	public class BreakpointHandle
	{
		Breakpoint breakpoint;
		SourceLocation location;

		internal BreakpointHandle (Process process, Breakpoint breakpoint,
					   SourceLocation location)
		{
			this.breakpoint = breakpoint;
			this.location = location;

			initialize (process);
		}

		internal BreakpointHandle (Process process, Breakpoint breakpoint,
					   TargetAddress address)
		{
			this.breakpoint = breakpoint;
			this.address = address;

			EnableBreakpoint (process);
		}

		public Breakpoint Breakpoint {
			get { return breakpoint; }
		}

		void initialize (Process process)
		{
			if (location.Method.IsLoaded) {
				address = location.GetAddress ();
				EnableBreakpoint (process);
			} else if (location.Method.IsDynamic) {
				// A dynamic method is a method which may emit a
				// callback when it's loaded.  We register this
				// callback here and do the actual insertion when
				// the method is loaded.
				load_handler = location.Method.RegisterLoadHandler (
					new MethodLoadedHandler (method_loaded), null);
			}
		}

		IDisposable load_handler;

		// <summary>
		//   The method has just been loaded, lookup the breakpoint
		//   address and actually insert it.
		// </summary>
		void method_loaded (Process process, SourceMethod method, object user_data)
		{
			load_handler = null;

			address = location.GetAddress ();
			if (address.IsNull)
				return;

			EnableBreakpoint (process);
		}

		TargetAddress address = TargetAddress.Null;
		int breakpoint_id = -1;

		public bool IsEnabled {
			get { return breakpoint_id > 0; }
		}

		public TargetAddress Address {
			get { return address; }
		}

		protected void Enable (Process process)
		{
			lock (this) {
				if ((address.IsNull) || (breakpoint_id > 0))
					return;

				breakpoint_id = process.InsertBreakpoint (breakpoint, address);
			}
		}

		protected void Disable (Process process)
		{
			lock (this) {
				if (breakpoint_id > 0)
					process.RemoveBreakpoint (breakpoint_id);

				breakpoint_id = -1;
			}
		}

		public void EnableBreakpoint (Process process)
		{
			lock (this) {
				Enable (process);
			}
		}

		public void DisableBreakpoint (Process process)
		{
			lock (this) {
				Disable (process);
			}
		}

		public void RemoveBreakpoint (Process process)
		{
			if (load_handler != null) {
				load_handler.Dispose ();
				load_handler = null;
			}
			DisableBreakpoint (process);
		}
	}
}
