using System;
using System.IO;
using System.Text;
using ST = System.Threading;
using System.Configuration;
using System.Globalization;
using System.Reflection;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;

using Mono.Debugger.Backend;
using Mono.Debugger.Languages;

namespace Mono.Debugger.Backend
{
	internal abstract class ThreadServant : DebuggerMarshalByRefObject
	{
		protected ThreadServant (ThreadManager manager, Process process)
		{
			this.manager = manager;
			this.process = process;

			this.id = manager.NextThreadID;

			tgroup = process.Session.CreateThreadGroup ("@" + ID);
			tgroup.AddThread (ID);

			thread = process.Debugger.CreateThread (this, ID);
		}

		protected readonly int id;
		protected readonly Thread thread;
		protected readonly Process process;
		protected readonly ThreadManager manager;
		protected readonly ThreadGroup tgroup;

		protected internal Language NativeLanguage {
			get { return process.NativeLanguage; }
		}

		public override string ToString ()
		{
			return Name;
		}

		public int ID {
			get { return id; }
		}

		public string Name {
			get {
				if (IsDaemon)
					return String.Format ("Daemon thread @{0}", id);
				else
					return String.Format ("Thread @{0}", id);
			}
		}

		public Thread Client {
			get { return thread; }
		}

		public abstract int PID {
			get;
		}

		public abstract long TID {
			get;
		}

		public abstract TargetAddress LMFAddress {
			get;
		}

		internal abstract ThreadManager ThreadManager {
			get;
		}

		internal Process Process {
			get { return process; }
		}

		public bool IsDaemon {
			get { return (thread.ThreadFlags & (Thread.Flags.Daemon | Thread.Flags.Immutable)) != 0; }
		}

		public ThreadGroup ThreadGroup {
			get { return tgroup; }
		}

		public abstract TargetEventArgs LastTargetEvent {
			get;
		}

		internal abstract object DoTargetAccess (TargetAccessHandler func);

		public abstract TargetMemoryArea[] GetMemoryMaps ();

		public abstract Method Lookup (TargetAddress address);

		public abstract Symbol SimpleLookup (TargetAddress address, bool exact_match);

		// <summary>
		//   The current method  May only be used when the engine is stopped
		//   (State == TargetState.STOPPED).  The single stepping engine
		//   automatically computes the current frame and current method each time
		//   a stepping operation is completed.  This ensures that we do not
		//   unnecessarily compute this several times if more than one client
		//   accesses this property.
		// </summary>
		public abstract Method CurrentMethod {
			get;
		}

		public abstract TargetState State {
			get;
		}

		public abstract StackFrame CurrentFrame {
			get;
		}

		public abstract TargetAddress CurrentFrameAddress {
			get;
		}

		public abstract Backtrace CurrentBacktrace {
			get;
		}

		public abstract Backtrace GetBacktrace (Backtrace.Mode mode, int max_frames);

		public abstract CommandResult Step (ThreadingModel model, StepMode mode, StepFrame frame);

		internal abstract ThreadCommandResult Old_Step (StepMode mode, StepFrame frame);

		public abstract void Kill ();

		public abstract void Detach ();

		internal abstract void DetachThread ();

		public abstract void Stop ();

		internal abstract void SuspendUserThread ();

		internal abstract void ResumeUserThread (CommandResult result);

		internal abstract object Invoke (TargetAccessDelegate func, object data);

		// <summary>
		//   Insert a breakpoint at address @address.
		//
		//   Returns a number which may be passed to RemoveBreakpoint() to remove
		//   the breakpoint.
		// </summary>
		internal abstract void InsertBreakpoint (BreakpointHandle handle,
							 TargetAddress address, int domain);

		// <summary>
		//   Remove breakpoint @index.  @index is the breakpoint number which has
		//   been returned by InsertBreakpoint().
		// </summary>
		internal abstract void RemoveBreakpoint (BreakpointHandle handle);

		internal abstract void AcquireThreadLock ();

		internal abstract void ReleaseThreadLock ();

		public abstract string PrintObject (Style style, TargetObject obj, DisplayFormat format);

		public abstract string PrintType (Style style, TargetType type);

		internal abstract Inferior.CallbackFrame GetCallbackFrame (TargetAddress stack_pointer,
									   bool exact_match);

		internal abstract TargetFunctionType GetRuntimeInvokedFunction (long ID);

		public abstract void RuntimeInvoke (TargetFunctionType function,
						    TargetStructObject object_argument,
						    TargetObject[] param_objects,
						    RuntimeInvokeFlags flags,
						    RuntimeInvokeResult result);

		public abstract CommandResult CallMethod (TargetAddress method, long arg1,
							  long arg2);

		public abstract CommandResult CallMethod (TargetAddress method, long arg1,
							  long arg2, long arg3, string string_arg);

		public abstract CommandResult CallMethod (TargetAddress method, TargetAddress method_argument,
							  TargetObject object_argument);

		public abstract CommandResult Return (ReturnMode mode);

		internal abstract void AbortInvocation (long ID);

		public string PrintRegisters (StackFrame frame)
		{
			return Architecture.PrintRegisters (frame);
		}

		public abstract bool IsAlive {
			get;
		}

		public abstract bool CanRun {
			get;
		}

		public abstract bool CanStep {
			get;
		}

		public abstract bool IsStopped {
			get;
		}

		public abstract ST.WaitHandle WaitHandle {
			get;
		}

		public abstract TargetMemoryInfo TargetMemoryInfo {
			get;
		}

		internal abstract Architecture Architecture {
			get;
		}

		public int TargetAddressSize {
			get { return TargetMemoryInfo.TargetAddressSize; }
		}

		public int TargetIntegerSize {
			get { return TargetMemoryInfo.TargetIntegerSize; }
		}

		public int TargetLongIntegerSize {
			get { return TargetMemoryInfo.TargetLongIntegerSize; }
		}

		public bool IsBigEndian {
			get { return TargetMemoryInfo.IsBigEndian; }
		}

		public AddressDomain AddressDomain {
			get { return TargetMemoryInfo.AddressDomain; }
		}

		public abstract byte ReadByte (TargetAddress address);

		public abstract int ReadInteger (TargetAddress address);

		public abstract long ReadLongInteger (TargetAddress address);

		public abstract TargetAddress ReadAddress (TargetAddress address);

		public abstract string ReadString (TargetAddress address);

		public abstract TargetBlob ReadMemory (TargetAddress address, int size);

		public abstract byte[] ReadBuffer (TargetAddress address, int size);

		public abstract Registers GetRegisters ();

		public abstract bool CanWrite {
			get;
		}

		public abstract void WriteBuffer (TargetAddress address, byte[] buffer);

		public abstract void WriteByte (TargetAddress address, byte value);

		public abstract void WriteInteger (TargetAddress address, int value);

		public abstract void WriteLongInteger (TargetAddress address, long value);

		public abstract void WriteAddress (TargetAddress address, TargetAddress value);

		public abstract void SetRegisters (Registers registers);

		public abstract int GetInstructionSize (TargetAddress address);

		public abstract AssemblerLine DisassembleInstruction (Method method,
								      TargetAddress address);

		public abstract AssemblerMethod DisassembleMethod (Method method);

#region IDisposable implementation
		private bool disposed = false;

		protected void check_disposed ()
		{
			if (disposed)
				throw new ObjectDisposedException ("ThreadServant");
		}

		protected virtual void DoDispose ()
		{
		}

		protected virtual void Dispose (bool disposing)
		{
			// Check to see if Dispose has already been called.
			if (disposed)
				return;

			disposed = true;

			// If this is a call to Dispose, dispose all managed resources.
			if (disposing) {
				DoDispose ();
			}
		}

		public void Dispose ()
		{
			Dispose (true);
			// Take yourself off the Finalization queue
			GC.SuppressFinalize (this);
		}
#endregion

		~ThreadServant ()
		{
			Dispose (false);
		}
	}
}
