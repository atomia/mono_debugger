using System;
using System.IO;
using System.Collections;
using System.Runtime.InteropServices;
using Mono.Debugger.Backends;

namespace Mono.Debugger.Architecture
{
	internal abstract class CoreFile : IInferior
	{
		protected Bfd bfd;
		protected Bfd core_bfd;
		protected BfdContainer bfd_container;

		protected BfdDisassembler bfd_disassembler;
		protected IArchitecture arch;

		DebuggerBackend backend;
		SymbolTableManager symtab_manager;
		ISymbolTable current_symtab;

		public CoreFile (DebuggerBackend backend, string application, string core_file,
				 BfdContainer bfd_container)
		{
			this.backend = backend;
			this.symtab_manager = backend.SymbolTableManager;

			arch = new ArchitectureI386 (this);

			core_file = Path.GetFullPath (core_file);
			application = Path.GetFullPath (application);

			core_bfd = new Bfd (bfd_container, this, core_file, true, null, TargetAddress.Null);
			bfd = bfd_container.AddFile (this, application, false, TargetAddress.Null, core_bfd);
			bfd.ReadDwarf ();

			core_bfd.MainBfd = bfd;

			bfd_disassembler = bfd.GetDisassembler (this);

			string crash_program = Path.GetFullPath (core_bfd.CrashProgram);

			if (crash_program != application)
				throw new CannotStartTargetException (String.Format (
					"Core file (generated from {0}) doesn't match executable {1}.",
					crash_program, application));

			bool ok;
			try {
				DateTime core_date = Directory.GetLastWriteTime (core_file);
				DateTime app_date = Directory.GetLastWriteTime (application);

				ok = app_date < core_date;
			} catch {
				ok = false;
			}

			if (!ok)
				throw new CannotStartTargetException (String.Format (
					"Executable {0} is more recent than core file {1}.",
					application, core_file));

			try {
				ISymbolTable bfd_symtab = bfd.SymbolTable;
			} catch (Exception e) {
				Console.WriteLine ("Can't get native symbol table: {0}", e);
			}

			UpdateModules ();
		}

		public DebuggerBackend DebuggerBackend {
			get {
				return backend;
			}
		}

		public void UpdateModules ()
		{
			bfd.UpdateSharedLibraryInfo ();
			current_symtab = symtab_manager.SymbolTable;
		}

		bool has_current_method = false;
		IMethod current_method = null;

		public IMethod CurrentMethod {
			get {
				if (has_current_method)
					return current_method;

				has_current_method = true;
				if (current_symtab == null)
					return null;
				current_method = current_symtab.Lookup (IInferior.CurrentFrame);
				return current_method;
			}
		}

		bool has_current_frame = false;
		StackFrame current_frame = null;

		public StackFrame CurrentFrame {
			get {
				if (has_current_frame)
					return current_frame;

				TargetAddress address = IInferior.CurrentFrame;
				IMethod method = CurrentMethod;

				if ((method != null) && method.HasSource) {
					SourceLocation source = method.Source.Lookup (address);

					current_frame = new StackFrame (this, address, null, 0, source, method);
				} else
					current_frame = new StackFrame (this, address, null, 0);

				has_current_frame = true;
				return current_frame;
			}
		}

		bool has_backtrace = false;
		StackFrame[] backtrace = null;

		public StackFrame[] GetBacktrace ()
		{
			if (has_backtrace)
				return backtrace;

			IInferiorStackFrame[] frames = GetBacktrace (-1, TargetAddress.Null);
			backtrace = new StackFrame [frames.Length];

			for (int i = 0; i < frames.Length; i++) {
				TargetAddress address = frames [i].Address;

				IMethod method = null;
				if (current_symtab != null)
					method = current_symtab.Lookup (address);
				if ((method != null) && method.HasSource) {
					SourceLocation source = method.Source.Lookup (address);
					backtrace [i] = new StackFrame (
						this, address, frames [i], i, source, method);
				} else
					backtrace [i] = new StackFrame (
						this, address, frames [i], i);
			}

			has_backtrace = true;
			return backtrace;
		}

		protected class CoreFileStackFrame : IInferiorStackFrame
		{
			IInferior inferior;
			TargetAddress address;
			TargetAddress params_address;
			TargetAddress locals_address;

			public CoreFileStackFrame (IInferior inferior, long address,
						   long params_address, long locals_address)
			{
				this.inferior = inferior;
				this.address = new TargetAddress (inferior, address);
				this.params_address = new TargetAddress (inferior, params_address);
				this.locals_address = new TargetAddress (inferior, locals_address);
			}

			public IInferior Inferior {
				get {
					return inferior;
				}
			}

			public TargetAddress Address {
				get {
					return address;
				}
			}

			public TargetAddress ParamsAddress {
				get {
					return params_address;
				}
			}

			public TargetAddress LocalsAddress {
				get {
					return locals_address;
				}
			}
		}

		//
		// IInferior
		//

		protected abstract TargetAddress GetCurrentFrame ();

		TargetAddress IInferior.CurrentFrame {
			get {
				return GetCurrentFrame ();
			}
		}

		public TargetAddress SimpleLookup (string name)
		{
			return bfd [name];
		}

		public abstract long GetRegister (int register);

		public abstract long[] GetRegisters (int[] registers);

		public abstract IInferiorStackFrame[] GetBacktrace (int max_frames, TargetAddress stop);

		public IDisassembler Disassembler {
			get {
				check_disposed ();
				return bfd_disassembler;
			}
		}

		public IArchitecture Architecture {
			get {
				check_disposed ();
				return arch;
			}
		}

		public Module[] Modules {
			get {
				return new Module[] { bfd.Module };
			}
		}

		public TargetMemoryArea[] GetMemoryMaps ()
		{
			return core_bfd.GetMemoryMaps ();
		}

		//
		// ITargetNotification
		//

		public TargetState State {
			get {
				return TargetState.CORE_FILE;
			}
		}

		public event TargetOutputHandler TargetOutput;
		public event TargetOutputHandler TargetError;
		public event TargetOutputHandler DebuggerOutput;
		public event DebuggerErrorHandler DebuggerError;
		public event StateChangedHandler StateChanged;
		public event TargetExitedHandler TargetExited;
		public event ChildEventHandler ChildEvent;

		//
		// ITargetInfo
		//

		public int TargetAddressSize {
			get {
				// FIXME
				return 4;
			}
		}

		public int TargetIntegerSize {
			get {
				// FIXME
				return 4;
			}
		}

		public int TargetLongIntegerSize {
			get {
				// FIXME
				return 8;
			}
		}

		public int StopSignal {
			get {
				throw new CannotExecuteCoreFileException ();
			}
		}

		//
		// ITargetMemoryAccess
		//

		public byte ReadByte (TargetAddress address)
		{
			return core_bfd.GetReader (address).ReadByte ();
		}

		public int ReadInteger (TargetAddress address)
		{
			return core_bfd.GetReader (address).ReadInteger ();
		}

		public long ReadLongInteger (TargetAddress address)
		{
			return core_bfd.GetReader (address).ReadLongInteger ();
		}

		public TargetAddress ReadAddress (TargetAddress address)
		{
			return core_bfd.GetReader (address).ReadAddress ();
		}

		public string ReadString (TargetAddress address)
		{
			return core_bfd.GetReader (address).BinaryReader.ReadString ();
		}

		public ITargetMemoryReader ReadMemory (TargetAddress address, int size)
		{
			return new TargetReader (ReadBuffer (address, size), this);
		}

		public byte[] ReadBuffer (TargetAddress address, int size)
		{
			return core_bfd.GetReader (address).BinaryReader.ReadBuffer (size);
		}

		public bool CanWrite {
			get {
				return false;
			}
		}

		public void WriteBuffer (TargetAddress address, byte[] buffer, int size)
		{
			throw new InvalidOperationException ();
		}

		public void WriteByte (TargetAddress address, byte value)
		{
			throw new InvalidOperationException ();
		}

		public void WriteInteger (TargetAddress address, int value)
		{
			throw new InvalidOperationException ();
		}

		public void WriteLongInteger (TargetAddress address, long value)
		{
			throw new InvalidOperationException ();
		}

		public void WriteAddress (TargetAddress address, TargetAddress value)
		{
			throw new InvalidOperationException ();
		}

		public TargetAddress MainMethodAddress {
			get {
				throw new NotImplementedException ();
			}
		}

		public TargetAddress GetReturnAddress ()
		{
			throw new NotImplementedException ();
		}

		//
		// IInferior - everything below throws a CannotExecuteCoreFileException.
		//

		public SingleSteppingEngine SingleSteppingEngine {
			get {
				throw new CannotExecuteCoreFileException ();
			}

			set {
				throw new CannotExecuteCoreFileException ();
			}
		}

		public int PID {
			get {
				throw new CannotExecuteCoreFileException ();
			}
		}

		public bool CurrentInstructionIsBreakpoint {
			get {
				throw new CannotExecuteCoreFileException ();
			}
		}

		public void Continue ()
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void Shutdown ()
		{
			// Do nothing.
		}

		public void Kill ()
		{
			// Do nothing.
		}

		public void Run ()
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void Attach (int pid)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public ChildEvent Wait ()
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void Step ()
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void SetSignal (int signal, bool send_it)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void Stop ()
		{
			throw new CannotExecuteCoreFileException ();
		}

		public long CallMethod (TargetAddress method, long method_argument)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public long CallStringMethod (TargetAddress method, long method_argument,
					      string string_argument)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public TargetAddress CallInvokeMethod (TargetAddress invoke_method,
						       TargetAddress method_argument,
						       TargetAddress object_argument,
						       TargetAddress[] param_objects,
						       out TargetAddress exc_object)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public int InsertBreakpoint (TargetAddress address)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public int InsertHardwareBreakpoint (TargetAddress address, int index)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void RemoveBreakpoint (int breakpoint)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void EnableBreakpoint (int breakpoint)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void DisableBreakpoint (int breakpoint)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void EnableAllBreakpoints ()
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void DisableAllBreakpoints ()
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void SetRegister (int register, long value)
		{
			throw new CannotExecuteCoreFileException ();
		}

		public void SetRegisters (int[] registers, long[] values)
		{
			throw new CannotExecuteCoreFileException ();
		}

		//
		// IDisposable
		//

		private void check_disposed ()
		{
			if (disposed)
				throw new ObjectDisposedException ("Inferior");
		}

		private bool disposed = false;

		protected virtual void Dispose (bool disposing)
		{
			// Check to see if Dispose has already been called.
			if (!this.disposed) {
				// If this is a call to Dispose,
				// dispose all managed resources.
				if (disposing) {
					bfd_container.CloseBfd (bfd);
					if (core_bfd != null)
						core_bfd.Dispose ();
				}
				
				this.disposed = true;

				lock (this) {
					// Release unmanaged resources
				}
			}
		}

		public void Dispose ()
		{
			Dispose (true);
			// Take yourself off the Finalization queue
			GC.SuppressFinalize (this);
		}

		~CoreFile ()
		{
			Dispose (false);
		}
	}
}
