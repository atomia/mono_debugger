using GLib;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Configuration;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;

using Mono.Debugger;
using Mono.Debugger.Languages;
using Mono.Debugger.Architecture;
using Mono.CSharp.Debugger;

namespace Mono.Debugger.Backends
{
	internal enum ChildMessageType {
		CHILD_EXITED = 1,
		CHILD_STOPPED,
		CHILD_SIGNALED,
		CHILD_CALLBACK,
		CHILD_HIT_BREAKPOINT
	}

	internal enum CommandError {
		NONE = 0,
		IO,
		UNKNOWN,
		INVALID_COMMAND,
		NOT_STOPPED
	}
	
	internal enum ServerCommand {
		GET_PC = 1,
		DETACH,
		SHUTDOWN,
		KILL,
		CONTINUE,
		STEP
	}

	internal delegate void ChildSetupHandler ();
	internal delegate void ChildExitedHandler ();
	internal delegate void ChildCallbackHandler (long argument, long data);
	internal delegate void ChildMessageHandler (ChildMessageType message, int arg);

	internal class Inferior : IInferior, IDisposable
	{
		IntPtr server_handle, g_source;
		IOOutputChannel inferior_stdin;
		IOInputChannel inferior_stdout;
		IOInputChannel inferior_stderr;

		string working_directory;
		string[] argv;
		string[] envp;

		Bfd bfd;
		BfdDisassembler bfd_disassembler;
		IArchitecture arch;
		ISymbolTableCollection native_symtabs;
		ISourceFileFactory source_factory;

		int child_pid;
		bool native;

		ITargetInfo target_info;
		Hashtable pending_callbacks = new Hashtable ();
		long last_callback_id = 0;

		IStepFrame current_step_frame = null;

		public int PID {
			get {
				check_disposed ();
				return child_pid;
			}
		}

		public event ChildExitedHandler ChildExited;
		public event ChildMessageHandler ChildMessage;

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_spawn (IntPtr handle, string working_directory, string[] argv, string[] envp, bool search_path, ChildExitedHandler child_exited, ChildMessageHandler child_message, ChildCallbackHandler child_callback, out int child_pid, out int standard_input, out int standard_output, out int standard_error, out IntPtr error);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_attach (IntPtr handle, int child_pid, ChildExitedHandler child_exited, ChildMessageHandler child_message, ChildCallbackHandler child_callback);

		[DllImport("monodebuggerserver")]
		static extern IntPtr mono_debugger_server_get_g_source (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern void mono_debugger_server_wait (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_get_pc (IntPtr handle, out long pc);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_step (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_continue (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_detach (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_finalize (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_read_memory (IntPtr handle, long start, int size, out IntPtr data);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_write_memory (IntPtr handle, IntPtr data, long start, int size);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_get_target_info (IntPtr handle, out int target_int_size, out int target_long_size, out int target_address_size);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_call_method (IntPtr handle, long method_address, long method_argument, long callback_argument);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_insert_breakpoint (IntPtr handle, long address, out int breakpoint);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_remove_breakpoint (IntPtr handle, int breakpoint);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_get_registers (IntPtr handle, int count, IntPtr registers, IntPtr values);

		[DllImport("monodebuggerglue")]
		static extern void mono_debugger_glue_kill_process (int pid, bool force);

		[DllImport("monodebuggerserver")]
		static extern IntPtr mono_debugger_server_initialize ();

		[DllImport("glib-2.0")]
		extern static uint g_source_attach (IntPtr source, IntPtr context);

		[DllImport("glib-2.0")]
		extern static void g_source_destroy (IntPtr source);

		[DllImport("glib-2.0")]
		extern static void g_free (IntPtr data);

		void check_error (CommandError error)
		{
			if (error == CommandError.NONE)
				return;

			handle_error (error);
		}

		void handle_error (CommandError error)
		{
			switch (error) {
			case CommandError.NOT_STOPPED:
				throw new TargetNotStoppedException ();

			default:
				throw new TargetException (
					"Got unknown error condition from inferior: " + error);
			}
		}

		internal TargetAsyncResult call_method (TargetAddress method, long method_argument,
							TargetAsyncCallback callback, object user_data)
		{
			check_disposed ();
			long number = ++last_callback_id;
			TargetAsyncResult async = new TargetAsyncResult (callback, user_data);
			pending_callbacks.Add (number, async);

			TargetState old_state = change_target_state (TargetState.RUNNING);
			try {
				check_error (mono_debugger_server_call_method (
					server_handle, method.Address, method_argument, number));
			} catch {
				change_target_state (old_state);
			}
			return async;
		}

		public long CallMethod (TargetAddress method, long method_argument)
		{
			check_disposed ();
			TargetAsyncResult result = call_method (
				method, method_argument, null, null);
			mono_debugger_server_wait (server_handle);
			if (!result.IsCompleted)
				throw new TargetException ("Call not completed");
			return (long) result.AsyncResult;
		}

		int insert_breakpoint (TargetAddress address)
		{
			int retval;
			check_error (mono_debugger_server_insert_breakpoint (
				server_handle, address.Address, out retval));
			return retval;
		}

		void remove_breakpoint (int breakpoint)
		{
			check_error (mono_debugger_server_remove_breakpoint (
				server_handle, breakpoint));
		}

		int temp_breakpoint_id = 0;
		void insert_temporary_breakpoint (TargetAddress address)
		{
			temp_breakpoint_id = insert_breakpoint (address);
		}

		public Inferior (string working_directory, string[] argv, string[] envp, bool native,
				 ISourceFileFactory factory)
		{
			this.working_directory = working_directory;
			this.argv = argv;
			this.envp = envp;
			this.native = native;
			this.source_factory = factory;

			int stdin_fd, stdout_fd, stderr_fd;
			IntPtr error;

			bfd = new Bfd (this, argv [0], source_factory);

			server_handle = mono_debugger_server_initialize ();
			if (server_handle == IntPtr.Zero)
				throw new TargetException ("Can't get server handle");

			check_error (mono_debugger_server_spawn (
				server_handle, working_directory, argv, envp, true,
				new ChildExitedHandler (child_exited),
				new ChildMessageHandler (child_message),
				new ChildCallbackHandler (child_callback),
				out child_pid, out stdin_fd, out stdout_fd, out stderr_fd,
				out error));

			inferior_stdin = new IOOutputChannel (stdin_fd);
			inferior_stdout = new IOInputChannel (stdout_fd);
			inferior_stderr = new IOInputChannel (stderr_fd);

			setup_inferior ();
		}

		public Inferior (int pid, string[] envp, ISourceFileFactory factory)
		{
			this.envp = envp;
			this.source_factory = factory;

			bfd = new Bfd (this, argv [0], factory);

			server_handle = mono_debugger_server_initialize ();
			if (server_handle == IntPtr.Zero)
				throw new TargetException ("Can't get server handle");

			check_error (mono_debugger_server_attach (
				server_handle, pid, new ChildExitedHandler (child_exited),
				new ChildMessageHandler (child_message),
				new ChildCallbackHandler (child_callback)));

			setup_inferior ();
		}

		void setup_inferior ()
		{
			inferior_stdout.ReadLine += new ReadLineHandler (inferior_output);
			inferior_stderr.ReadLine += new ReadLineHandler (inferior_errors);

			g_source = mono_debugger_server_get_g_source (server_handle);
			if (g_source == IntPtr.Zero)
				handle_error (CommandError.UNKNOWN);

			g_source_attach (g_source, IntPtr.Zero);

			int target_int_size, target_long_size, target_address_size;
			check_error (mono_debugger_server_get_target_info
				(server_handle, out target_int_size, out target_long_size,
				 out target_address_size));

			target_info = new TargetInfo (target_int_size, target_long_size,
						      target_address_size);

			bfd_disassembler = bfd.GetDisassembler (this);
			arch = new ArchitectureI386 (this);

			native_symtabs = new SymbolTableCollection ();
			bfd_disassembler.SymbolTable = native_symtabs;

			try {
				ISymbolTable bfd_symtab = bfd.SymbolTable;
				if (bfd_symtab != null)
					native_symtabs.AddSymbolTable (bfd_symtab);
			} catch (Exception e) {
				Console.WriteLine ("Can't get native symbol table: {0}", e);
			}
		}

		public TargetAddress SimpleLookup (string name)
		{
			return bfd [name];
		}

		bool start_native ()
		{
			if (!native)
				return false;

			TargetAddress symbol_info = bfd ["main"];
			if (symbol_info.IsNull)
				return false;

			insert_temporary_breakpoint (symbol_info);
			return true;
		}

		void child_exited ()
		{
			child_pid = 0;
			Dispose ();
			if (ChildExited != null)
				ChildExited ();
		}

		void child_callback (long callback, long data)
		{
			change_target_state (TargetState.STOPPED);

			if (!pending_callbacks.Contains (callback))
				return;

			TargetAsyncResult async = (TargetAsyncResult) pending_callbacks [callback];
			pending_callbacks.Remove (callback);

			async.Completed (data);
		}

		bool initialized;
		bool debugger_info_read;
		void child_message (ChildMessageType message, int arg)
		{
			if (temp_breakpoint_id != 0) {
				remove_breakpoint (temp_breakpoint_id);
				temp_breakpoint_id = 0;
				if (message == ChildMessageType.CHILD_HIT_BREAKPOINT) {
					child_message (ChildMessageType.CHILD_STOPPED, 0);
					return;
				}
			}

			switch (message) {
			case ChildMessageType.CHILD_STOPPED:
				if (!initialized) {
					initialized = true;
					if (!native || start_native ()) {
						Continue ();
						break;
					}
				} else if (current_step_frame != null) {
					TargetAddress frame = CurrentFrame;

					if ((frame >= current_step_frame.Start) &&
					    (frame < current_step_frame.End)) {
						Step (current_step_frame);
						break;
					}
					current_step_frame = null;
				}
				change_target_state (TargetState.STOPPED);
				break;

			case ChildMessageType.CHILD_EXITED:
			case ChildMessageType.CHILD_SIGNALED:
				change_target_state (TargetState.EXITED);
				break;

			case ChildMessageType.CHILD_HIT_BREAKPOINT:
				Console.WriteLine ("CHILD HIT BREAKPOINT: {0}", arg);
				child_message (ChildMessageType.CHILD_STOPPED, 0);
				break;

			default:
				Console.WriteLine ("CHILD MESSAGE: {0} {1}", message, arg);
				break;
			}

			if (ChildMessage != null)
				ChildMessage (message, arg);
		}

		void inferior_output (string line)
		{
			if (TargetOutput != null)
				TargetOutput (line);
		}

		void inferior_errors (string line)
		{
			if (TargetError != null)
				TargetError (line);
		}

		//
		// ITargetInfo
		//

		public int TargetIntegerSize {
			get {
				return target_info.TargetIntegerSize;
			}
		}

		public int TargetLongIntegerSize {
			get {
				return target_info.TargetLongIntegerSize;
			}
		}

		public int TargetAddressSize {
			get {
				return target_info.TargetAddressSize;
			}
		}

		//
		// ITargetMemoryAccess
		//

		IntPtr read_buffer (TargetAddress address, int size)
		{
			IntPtr data;
			CommandError result = mono_debugger_server_read_memory (
				server_handle, address.Address, size, out data);
			if (result != CommandError.NONE) {
				g_free (data);
				handle_error (result);
				throw new Exception ("Internal error: this line will never be reached");
			}
			return data;
		}

		public byte[] ReadBuffer (TargetAddress address, int size)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, size);
				byte[] retval = new byte [size];
				Marshal.Copy (data, retval, 0, size);
				return retval;
			} finally {
				g_free (data);
			}
		}

		public byte ReadByte (TargetAddress address)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, 1);
				return Marshal.ReadByte (data);
			} finally {
				g_free (data);
			}
		}

		public int ReadInteger (TargetAddress address)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, 4);
				return Marshal.ReadInt32 (data);
			} finally {
				g_free (data);
			}
		}

		public long ReadLongInteger (TargetAddress address)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, 8);
				return Marshal.ReadInt64 (data);
			} finally {
				g_free (data);
			}
		}

		public TargetAddress ReadAddress (TargetAddress address)
		{
			check_disposed ();
			switch (TargetAddressSize) {
			case 4:
				return new TargetAddress (this, ReadInteger (address));

			case 8:
				return new TargetAddress (this, ReadLongInteger (address));

			default:
				throw new TargetMemoryException (
					"Unknown target address size " + TargetAddressSize);
			}
		}

		public string ReadString (TargetAddress address)
		{
			check_disposed ();
			StringBuilder sb = new StringBuilder ();

			while (true) {
				byte b = ReadByte (address++);

				if (b == 0)
					return sb.ToString ();

				sb.Append ((char) b);
			}
		}

		public ITargetMemoryReader ReadMemory (TargetAddress address, int size)
		{
			check_disposed ();
			byte [] retval = ReadBuffer (address, size);
			return new TargetReader (retval, this);
		}

		public Stream GetMemoryStream (TargetAddress address)
		{
			check_disposed ();
			return new TargetMemoryStream (this, address, target_info);
		}

		public bool CanWrite {
			get {
				return false;
			}
		}

		public void WriteBuffer (TargetAddress address, byte[] buffer, int size)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (size);
				Marshal.Copy (buffer, 0, data, size);
				check_error (mono_debugger_server_write_memory (
					server_handle, data, address.Address, size));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
			}
		}

		public void WriteByte (TargetAddress address, byte value)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (1);
				Marshal.WriteByte (data, value);
				check_error (mono_debugger_server_write_memory (
					server_handle, data, address.Address, 1));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
			}
		}

		public void WriteInteger (TargetAddress address, int value)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (4);
				Marshal.WriteInt32 (data, value);
				check_error (mono_debugger_server_write_memory (
					server_handle, data, address.Address, 4));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
			}
		}

		public void WriteLongInteger (TargetAddress address, long value)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (8);
				Marshal.WriteInt64 (data, value);
				check_error (mono_debugger_server_write_memory (
					server_handle, data, address.Address, 8));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
			}
		}

		public void WriteAddress (TargetAddress address, TargetAddress value)
		{
			check_disposed ();
			switch (TargetAddressSize) {
			case 4:
				WriteInteger (address, (int) value.Address);
				break;

			case 8:
				WriteLongInteger (address, value.Address);
				break;

			default:
				throw new TargetMemoryException (
					"Unknown target address size " + TargetAddressSize);
			}
		}

		//
		// IInferior
		//

		public event TargetOutputHandler TargetOutput;
		public event TargetOutputHandler TargetError;
		public event StateChangedHandler StateChanged;

		TargetState target_state = TargetState.NO_TARGET;
		public TargetState State {
			get {
				check_disposed ();
				return target_state;
			}
		}

		TargetState change_target_state (TargetState new_state)
		{
			if (new_state == target_state)
				return target_state;

			TargetState old_state = target_state;
			target_state = new_state;

			if (StateChanged != null)
				StateChanged (target_state);

			return old_state;
		}

		public void Continue ()
		{
			check_disposed ();
			TargetState old_state = change_target_state (TargetState.RUNNING);
			try {
				check_error (mono_debugger_server_continue (server_handle));
			} catch {
				change_target_state (old_state);
			}
		}

		public void Continue (TargetAddress until)
		{
			check_disposed ();
			TargetAddress current = CurrentFrame;

			inferior_output (String.Format ("Requested to run from {0:x} until {1:x}.",
							current, until));

			while (current < until)
				current += bfd_disassembler.GetInstructionSize (current);

			if (current != until)
				inferior_output (String.Format (
					"Oooops: reached {0:x} but symfile had {1:x}",
					current, until));

			insert_temporary_breakpoint (current);
			Continue ();
		}

		public void Detach ()
		{
			check_disposed ();
			check_error (mono_debugger_server_detach (server_handle));
		}

		public void Shutdown ()
		{
			// send_command (ServerCommand.SHUTDOWN);
		}

		public void Kill ()
		{
			// send_command (ServerCommand.KILL);
		}

		public void Step ()
		{
			check_disposed ();
			Step (null);
		}

		public void Step (IStepFrame frame)
		{
			check_disposed ();
			int insn_size;
			TargetAddress call = arch.GetCallTarget (CurrentFrame, out insn_size);
			if (!native && !call.IsNull && (frame.Language != null)) {
				TargetAddress trampoline = frame.Language.GetTrampoline (call);

				Console.WriteLine ("CALL: {4:x} {3} - {0:x} {1} => {2:x}",
						   call, insn_size, trampoline, frame, CurrentFrame);

				if (!trampoline.IsNull) {
					insert_temporary_breakpoint (trampoline);
					Continue ();
					return;
				}
			} else if (!call.IsNull) {
				IMethod method = native_symtabs.Lookup (call);
				if (method == null) {
					Next ();
					return;
				}
			}

			current_step_frame = frame;

			TargetState old_state = change_target_state (TargetState.RUNNING);
			try {
				check_error (mono_debugger_server_step (server_handle));
			} catch {
				change_target_state (old_state);
			}
		}

		public void Next ()
		{
			check_disposed ();
			TargetAddress address = CurrentFrame;
			address += bfd_disassembler.GetInstructionSize (address);

			insert_temporary_breakpoint (address);
			Continue ();
		}

		public TargetAddress CurrentFrame {
			get {
				long pc;
				check_disposed ();
				CommandError result = mono_debugger_server_get_pc (server_handle, out pc);
				if (result != CommandError.NONE)
					throw new NoStackException ();

				return new TargetAddress (this, pc);
			}
		}

		public IDisassembler Disassembler {
			get {
				check_disposed ();
				return bfd_disassembler;
			}
		}

		public ISymbolTableCollection SymbolTable {
			get {
				check_disposed ();
				return native_symtabs;
			}
		}

		public IArchitecture Architecture {
			get {
				check_disposed ();
				return arch;
			}
		}

		public long[] GetRegisters (int[] registers)
		{
			IntPtr data = IntPtr.Zero, buffer = IntPtr.Zero;
			try {
				int size = registers.Length * 4;
				int buffer_size = registers.Length * 8;
				data = Marshal.AllocHGlobal (size);
				Marshal.Copy (registers, 0, data, registers.Length);
				buffer = Marshal.AllocHGlobal (buffer_size);
				CommandError result = mono_debugger_server_get_registers (
					server_handle, registers.Length, data, buffer);
				check_error (result);
				long[] retval = new long [registers.Length];
				Marshal.Copy (buffer, retval, 0, registers.Length);
				return retval;
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
				if (buffer != IntPtr.Zero)
					Marshal.FreeHGlobal (buffer);
			}
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
					if (bfd != null)
						bfd.Dispose ();
					if (bfd_disassembler != null)
						bfd_disassembler.Dispose ();
					// Do stuff here
				}
				
				// Release unmanaged resources
				this.disposed = true;

				lock (this) {
					if (g_source != IntPtr.Zero) {
						g_source_destroy (g_source);
						g_source = IntPtr.Zero;
					}
					if (server_handle != IntPtr.Zero) {
						mono_debugger_server_finalize (server_handle);
						server_handle = IntPtr.Zero;
					}
				}
			}
		}

		public void Dispose ()
		{
			Dispose (true);
			// Take yourself off the Finalization queue
			GC.SuppressFinalize (this);
		}

		~Inferior ()
		{
			Dispose (false);
		}
	}
}
