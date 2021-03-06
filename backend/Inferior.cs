using System;
using System.IO;
using System.Text;
using ST = System.Threading;
using System.Configuration;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;

using Mono.Debugger.Languages;

namespace Mono.Debugger.Backend
{
	internal delegate void ChildOutputHandler (bool is_stderr, string output);

	internal class Inferior : TargetAccess, ITargetNotification, IDisposable
	{
		protected IntPtr server_handle;
		protected IntPtr io_data;
		protected NativeExecutableReader exe;
		protected ThreadManager thread_manager;

		protected readonly ProcessStart start;

		protected readonly Process process;
		protected readonly DebuggerErrorHandler error_handler;
		protected readonly BreakpointManager breakpoint_manager;
		protected readonly AddressDomain address_domain;
		protected readonly bool native;

		int child_pid;
		bool initialized;
		bool has_target;
		bool pushed_regs;

		TargetMemoryInfo target_info;
		Architecture arch;

		bool has_signals;
		SignalInfo signal_info;

		public static bool IsRunningOnWindows {
			get {
				return ((int)Environment.OSVersion.Platform < 4);
			}
		}

		public bool HasTarget {
			get {
				return has_target;
			}
		}

		public int PID {
			get {
				check_disposed ();
				return child_pid;
			}
		}

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_initialize_process (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_initialize_thread (IntPtr handle, int child_pid, bool wait);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_io_thread_main (IntPtr io_data, ChildOutputHandler output_handler);
		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_spawn (IntPtr handle, string working_directory, string[] argv, string[] envp, bool redirect_fds, out int child_pid, out IntPtr io_data, out IntPtr error);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_attach (IntPtr handle, int child_pid);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_frame (IntPtr handle, out ServerStackFrame frame);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_current_insn_is_bpt (IntPtr handle, out int is_breakpoint);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_step (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_continue (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_resume (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_detach (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_finalize (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_read_memory (IntPtr handle, long start, int size, IntPtr data);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_write_memory (IntPtr handle, long start, int size, IntPtr data);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_target_info (out int target_int_size, out int target_long_size, out int target_address_size, out int is_bigendian);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_call_method (IntPtr handle, long method_address, long method_argument1, long method_argument2, long callback_argument);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_call_method_1 (IntPtr handle, long method_address, long method_argument, long data_argument, long data_argument2, string string_argument, long callback_argument);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_call_method_2 (IntPtr handle, long method_address, int data_size, IntPtr data, long callback_argument);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_call_method_3 (IntPtr handle, long method_address, long method_argument, long address_argument, int blob_size, IntPtr blob_data, long callback_argument);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_mark_rti_frame (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_abort_invoke (IntPtr handle, long rti_id);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_call_method_invoke (IntPtr handle, long invoke_method, long method_address, int num_params, int blob_size, IntPtr param_data, IntPtr offset_data, IntPtr blob_data, long callback_argument, bool debug);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_execute_instruction (IntPtr handle, IntPtr instruction, int insn_size, bool update_ip);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_insert_breakpoint (IntPtr handle, long address, out int breakpoint);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_insert_hw_breakpoint (IntPtr handle, HardwareBreakpointType type, out int index, long address, out int breakpoint);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_remove_breakpoint (IntPtr handle, int breakpoint);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_enable_breakpoint (IntPtr handle, int breakpoint);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_disable_breakpoint (IntPtr handle, int breakpoint);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_registers (IntPtr handle, IntPtr values);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_set_registers (IntPtr handle, IntPtr values);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_stop (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_stop_and_wait (IntPtr handle, out int status);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_set_signal (IntPtr handle, int signal, int send_it);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_pending_signal (IntPtr handle, out int signal);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_kill (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern IntPtr mono_debugger_server_create_inferior (IntPtr breakpoint_manager);

		[DllImport("monodebuggerserver")]
		static extern ChildEventType mono_debugger_server_dispatch_event (IntPtr handle, int status, out long arg, out long data1, out long data2, out int opt_data_size, out IntPtr opt_data);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_signal_info (IntPtr handle, out IntPtr data);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_threads (IntPtr handle, out int count, out IntPtr data);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_application (IntPtr handle, out IntPtr exe_file, out IntPtr cwd, out int nargs, out IntPtr data);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_detach_after_fork (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_push_registers (IntPtr handle, out long new_rsp);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_pop_registers (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_get_callback_frame (IntPtr handle, long stack_pointer, bool exact_match, IntPtr info);

		[DllImport("monodebuggerserver")]
		static extern TargetError mono_debugger_server_restart_notification (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern void mono_debugger_server_set_runtime_info (IntPtr handle, IntPtr mono_runtime_info);

		[DllImport("monodebuggerserver")]
		static extern ServerType mono_debugger_server_get_server_type ();

		[DllImport("monodebuggerserver")]
		static extern ServerCapabilities mono_debugger_server_get_capabilities ();

		internal enum ChildEventType {
			NONE = 0,
			UNKNOWN_ERROR = 1,
			CHILD_EXITED,
			CHILD_STOPPED,
			CHILD_SIGNALED,
			CHILD_CALLBACK,
			CHILD_CALLBACK_COMPLETED,
			CHILD_HIT_BREAKPOINT,
			CHILD_MEMORY_CHANGED,
			CHILD_CREATED_THREAD,
			CHILD_FORKED,
			CHILD_EXECD,
			CHILD_CALLED_EXIT,
			CHILD_NOTIFICATION,
			CHILD_INTERRUPTED,
			RUNTIME_INVOKE_DONE,
			INTERNAL_ERROR,

			UNHANDLED_EXCEPTION	= 4001,
			THROW_EXCEPTION,
			HANDLE_EXCEPTION
		}

		internal enum HardwareBreakpointType {
			NONE = 0,
			EXECUTE,
			READ,
			WRITE
		}

		internal enum ServerType {
			UNKNOWN = 0,
			LINUX_PTRACE = 1,
			DARWIN = 2,
			WINDOWS = 3
		}

		internal enum ServerCapabilities {
			NONE = 0,
			THREAD_EVENTS = 1,
			CAN_DETACH_ANY = 2
		}

		internal delegate void ChildEventHandler (ChildEventType message, int arg);

		internal sealed class ChildEvent
		{
			public readonly ChildEventType Type;
			public readonly long Argument;

			public readonly long Data1;
			public readonly long Data2;

			public readonly byte[] CallbackData;

			public ChildEvent (ChildEventType type, long arg, long data1, long data2)
			{
				this.Type = type;
				this.Argument = arg;
				this.Data1 = data1;
				this.Data2 = data2;
			}

			public ChildEvent (ChildEventType type, long arg, long data1, long data2,
					   byte[] callback_data)
				: this (type, arg, data1, data2)
			{
				this.CallbackData = callback_data;
			}

			public override string ToString ()
			{
				return String.Format ("ChildEvent ({0}:{1}:{2:x}:{3:x})",
						      Type, Argument, Data1, Data2);
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SignalInfo
		{
			public int SIGKILL;
			public int SIGSTOP;
			public int SIGINT;
			public int SIGCHLD;

			public int SIGFPE;
			public int SIGQUIT;
			public int SIGABRT;
			public int SIGSEGV;
			public int SIGILL;
			public int SIGBUS;
			public int SIGWINCH;

			public int Kernel_SIGRTMIN;
			public int MonoThreadAbortSignal;

			public override string ToString ()
			{
				return String.Format ("SignalInfo ({0}:{1}:{2}:{3}:{4} - {5})",
						      SIGKILL, SIGSTOP, SIGINT, SIGCHLD, Kernel_SIGRTMIN,
						      MonoThreadAbortSignal);
			}
		}

		protected Inferior (ThreadManager thread_manager, Process process,
				    ProcessStart start, BreakpointManager bpm,
				    DebuggerErrorHandler error_handler,
				    AddressDomain address_domain)
		{
			this.thread_manager = thread_manager;
			this.process = process;
			this.start = start;
			this.native = start.IsNative;
			this.error_handler = error_handler;
			this.breakpoint_manager = bpm;
			this.address_domain = address_domain;

			server_handle = mono_debugger_server_create_inferior (breakpoint_manager.Manager);
			if (server_handle == IntPtr.Zero)
				throw new InternalError ("mono_debugger_server_initialize() failed.");
		}

		public static Inferior CreateInferior (ThreadManager thread_manager,
						       Process process, ProcessStart start)
		{
			return new Inferior (
				thread_manager, process, start, process.BreakpointManager, null,
				thread_manager.AddressDomain);
		}

		public Inferior CreateThread (int pid, bool do_attach)
		{
			Inferior inferior = new Inferior (
				thread_manager, process, start, breakpoint_manager,
				error_handler, address_domain);

			inferior.child_pid = pid;

			inferior.signal_info = signal_info;
			inferior.has_signals = has_signals;

			inferior.target_info = target_info;
			inferior.exe = exe;

			inferior.arch = inferior.process.Architecture;

			if (do_attach)
				inferior.Attach (pid);
			else
				inferior.InitializeThread (pid);

			return inferior;
		}

		[DllImport("libglib-2.0-0.dll")]
		protected extern static void g_free (IntPtr data);

		protected static void check_error (TargetError error)
		{
			if (error == TargetError.None)
				return;

			throw new TargetException (error);
		}

		public void CallMethod (TargetAddress method, long data1, long data2,
					long callback_arg)
		{
			check_disposed ();

			TargetState old_state = change_target_state (TargetState.Busy);
			try {
				check_error (mono_debugger_server_call_method (
					server_handle, method.Address, data1, data2,
					callback_arg));
			} catch {
				change_target_state (old_state);
				throw;
			}
		}

		public void CallMethod (TargetAddress method, long arg1, long arg2, long arg3,
					string arg4, long callback_arg)
		{
			check_disposed ();

			TargetState old_state = change_target_state (TargetState.Running);
			try {
				check_error (mono_debugger_server_call_method_1 (
					server_handle, method.Address, arg1, arg2, arg3,
					arg4, callback_arg));
			} catch {
				change_target_state (old_state);
				throw;
			}
		}

		public void CallMethod (TargetAddress method, byte[] data, long callback_arg)
		{
			check_disposed ();

			TargetState old_state = change_target_state (TargetState.Running);

			IntPtr data_ptr = IntPtr.Zero;
			int data_size = data != null ? data.Length : 0;

			try {
				if (data != null) {
					data_ptr = Marshal.AllocHGlobal (data_size);
					Marshal.Copy (data, 0, data_ptr, data_size);
				}

				check_error (mono_debugger_server_call_method_2 (
					server_handle, method.Address,
					data_size, data_ptr, callback_arg));
			} catch {
				change_target_state (old_state);
				throw;
			} finally {
				if (data_ptr != IntPtr.Zero)
					Marshal.FreeHGlobal (data_ptr);
			}
		}

		public void CallMethod (TargetAddress method, long method_argument,
					TargetObject obj, long callback_arg)
		{
			check_disposed ();

			byte[] blob = null;
			long address = 0;

			if (obj.Location.HasAddress)
				address = obj.Location.GetAddress (this).Address;
			else
				blob = obj.Location.ReadBuffer (this, obj.Type.Size);

			IntPtr blob_data = IntPtr.Zero;
			try {
				if (blob != null) {
					blob_data = Marshal.AllocHGlobal (blob.Length);
					Marshal.Copy (blob, 0, blob_data, blob.Length);
				}

				check_error (mono_debugger_server_call_method_3 (
					server_handle, method.Address, method_argument,
					address, blob != null ? blob.Length : 0, blob_data, callback_arg));
			} finally {
				if (blob_data != IntPtr.Zero)
					Marshal.FreeHGlobal (blob_data);
			}
		}

		public void RuntimeInvoke (TargetAddress invoke_method,
					   TargetAddress method_argument,
					   TargetObject object_argument,
					   TargetObject[] param_objects,
					   long callback_arg, bool debug)
		{
			check_disposed ();

			int length = param_objects.Length + 1;

			TargetObject[] input_objects = new TargetObject [length];
			input_objects [0] = object_argument;
			param_objects.CopyTo (input_objects, 1);

			int blob_size = 0;
			byte[][] blobs = new byte [length][];
			int[] blob_offsets = new int [length];
			long[] addresses = new long [length];

			for (int i = 0; i < length; i++) {
				TargetObject obj = input_objects [i];

				if (obj == null)
					continue;
				if (obj.Location.HasAddress) {
					blob_offsets [i] = -1;
					addresses [i] = obj.Location.GetAddress (this).Address;
					continue;
				}
				blobs [i] = obj.Location.ReadBuffer (this, obj.Type.Size);
				blob_offsets [i] = blob_size;
				blob_size += blobs [i].Length;
			}

			byte[] blob = new byte [blob_size];
			blob_size = 0;
			for (int i = 0; i < length; i++) {
				if (blobs [i] == null)
					continue;
				blobs [i].CopyTo (blob, blob_size);
				blob_size += blobs [i].Length;
			}

			IntPtr blob_data = IntPtr.Zero, param_data = IntPtr.Zero;
			IntPtr offset_data = IntPtr.Zero;
			try {
				if (blob_size > 0) {
					blob_data = Marshal.AllocHGlobal (blob_size);
					Marshal.Copy (blob, 0, blob_data, blob_size);
				}

				param_data = Marshal.AllocHGlobal (length * 8);
				Marshal.Copy (addresses, 0, param_data, length);

				offset_data = Marshal.AllocHGlobal (length * 4);
				Marshal.Copy (blob_offsets, 0, offset_data, length);

				check_error (mono_debugger_server_call_method_invoke (
					server_handle, invoke_method.Address, method_argument.Address,
					length, blob_size, param_data, offset_data, blob_data,
					callback_arg, debug));
			} finally {
				if (blob_data != IntPtr.Zero)
					Marshal.FreeHGlobal (blob_data);
				Marshal.FreeHGlobal (param_data);
				Marshal.FreeHGlobal (offset_data);
			}
		}

		public void ExecuteInstruction (byte[] instruction, bool update_ip)
		{
			check_disposed ();

			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (instruction.Length);
				Marshal.Copy (instruction, 0, data, instruction.Length);

				check_error (mono_debugger_server_execute_instruction (
					server_handle, data, instruction.Length, update_ip));
			} finally {
				Marshal.FreeHGlobal (data);
			}
		}

		public void MarkRuntimeInvokeFrame ()
		{
			check_error (mono_debugger_server_mark_rti_frame (server_handle));
		}

		public void AbortInvoke (long rti_id)
		{
			TargetError result = mono_debugger_server_abort_invoke (server_handle, rti_id);
			check_error (result);
		}

		public int InsertBreakpoint (TargetAddress address)
		{
			int retval;
			check_error (mono_debugger_server_insert_breakpoint (
				server_handle, address.Address, out retval));
			return retval;
		}

		public int InsertHardwareBreakpoint (TargetAddress address, bool fallback,
						     out int index)
		{
			int retval;
			TargetError result = mono_debugger_server_insert_hw_breakpoint (
				server_handle, HardwareBreakpointType.NONE, out index,
				address.Address, out retval);
			if (result == TargetError.None)
				return retval;
			else if (fallback &&
				 ((result == TargetError.DebugRegisterOccupied) ||
				  (result == TargetError.NotImplemented))) {
				index = -1;
				return InsertBreakpoint (address);
			} else {
				throw new TargetException (result);
			}
		}

		public void RemoveBreakpoint (int breakpoint)
		{
			check_error (mono_debugger_server_remove_breakpoint (
				server_handle, breakpoint));
		}

		public int InsertHardwareWatchPoint (TargetAddress address,
						     HardwareBreakpointType type,
						     out int index)
		{
			int retval;
			check_error (mono_debugger_server_insert_hw_breakpoint (
				server_handle, type, out index, address.Address, out retval));
			return retval;
		}

		public void EnableBreakpoint (int breakpoint)
		{
			check_error (mono_debugger_server_enable_breakpoint (
				server_handle, breakpoint));
		}

		public void DisableBreakpoint (int breakpoint)
		{
			check_error (mono_debugger_server_disable_breakpoint (
				server_handle, breakpoint));
		}

		public void RestartNotification ()
		{
			check_error (mono_debugger_server_restart_notification (server_handle));
		}

		public ProcessStart ProcessStart {
			get {
				return start;
			}
		}

		public Process Process {
			get {
				return process;
			}
		}

		void io_thread_main ()
		{
			mono_debugger_server_io_thread_main (io_data, process.OnTargetOutput);
		}

		public int Run ()
		{
			if (has_target)
				throw new TargetException (TargetError.AlreadyHaveTarget);

			has_target = true;

			IntPtr error;

			string[] args = new string[start.CommandLineArguments.Length + 1];
			Array.Copy(start.CommandLineArguments, args, start.CommandLineArguments.Length);
			string[] env = new string[start.Environment.Length + 1];
			Array.Copy(start.Environment, env, start.Environment.Length);
			TargetError result = mono_debugger_server_spawn (
				server_handle, start.WorkingDirectory, args,
				env, start.RedirectOutput, out child_pid, out io_data, out error);
			if (result != TargetError.None) {
				string message = Marshal.PtrToStringAuto (error);
				g_free (error);

				throw new TargetException (
					TargetError.CannotStartTarget, message);
			}

			if (start.RedirectOutput) {
				ST.Thread io_thread = new ST.Thread (new ST.ThreadStart (io_thread_main));
				io_thread.IsBackground = true;
				io_thread.Start ();
			}

			initialized = true;

			check_error (mono_debugger_server_initialize_process (server_handle));

			SetupInferior ();

			change_target_state (TargetState.Stopped, 0);

			return child_pid;
		}

		public void InitializeThread (int pid)
		{
			if (initialized)
				throw new TargetException (TargetError.AlreadyHaveTarget);

			initialized = true;

			bool pending_sigstop = process.ThreadManager.HasPendingSigstopForNewThread (pid);

			check_error (mono_debugger_server_initialize_thread (server_handle, pid, !pending_sigstop));
			this.child_pid = pid;

			SetupInferior ();

			change_target_state (TargetState.Stopped, 0);
		}

		public void InitializeAfterExec (int pid)
		{
			if (initialized)
				throw new TargetException (TargetError.AlreadyHaveTarget);

			initialized = true;

			check_error (mono_debugger_server_initialize_thread (server_handle, pid, false));
			this.child_pid = pid;

			string exe_file, cwd;
			string[] cmdline_args;
			exe_file = GetApplication (out cwd, out cmdline_args);

			start.SetupApplication (exe_file, cwd, cmdline_args);

			SetupInferior ();

			change_target_state (TargetState.Stopped, 0);
		}

		public void Attach (int pid)
		{
			if (has_target || initialized)
				throw new TargetException (TargetError.AlreadyHaveTarget);

			has_target = true;

			check_error (mono_debugger_server_attach (server_handle, pid));
			this.child_pid = pid;

			string exe_file, cwd;
			string[] cmdline_args;
			exe_file = GetApplication (out cwd, out cmdline_args);

			start.SetupApplication (exe_file, cwd, cmdline_args);

			initialized = true;

			SetupInferior ();

			change_target_state (TargetState.Stopped, 0);
		}

		public ChildEvent ProcessEvent (int status)
		{
			long arg, data1, data2;
			ChildEventType message;

			int opt_data_size;
			IntPtr opt_data;

			message = mono_debugger_server_dispatch_event (
				server_handle, status, out arg, out data1, out data2,
				out opt_data_size, out opt_data);

			switch (message) {
			case ChildEventType.CHILD_EXITED:
			case ChildEventType.CHILD_SIGNALED:
				change_target_state (TargetState.Exited);
				break;

			case ChildEventType.CHILD_CALLBACK:
			case ChildEventType.CHILD_CALLBACK_COMPLETED:
			case ChildEventType.RUNTIME_INVOKE_DONE:
			case ChildEventType.CHILD_STOPPED:
			case ChildEventType.CHILD_INTERRUPTED:
			case ChildEventType.CHILD_HIT_BREAKPOINT:
			case ChildEventType.CHILD_NOTIFICATION:
				change_target_state (TargetState.Stopped);
				break;

			case ChildEventType.CHILD_EXECD:
				break;
			}

			if (opt_data_size > 0) {
				byte[] data = new byte [opt_data_size];
				Marshal.Copy (opt_data, data, 0, opt_data_size);
				g_free (opt_data);

				return new ChildEvent (message, arg, data1, data2, data);
			}

			return new ChildEvent (message, arg, data1, data2);
		}

		public static TargetInfo GetTargetInfo ()
		{
			int target_int_size, target_long_size, target_addr_size, is_bigendian;
			check_error (mono_debugger_server_get_target_info
				(out target_int_size, out target_long_size,
				 out target_addr_size, out is_bigendian));

			return new TargetInfo (target_int_size, target_long_size,
					       target_addr_size, is_bigendian != 0);
		}

		public static TargetMemoryInfo GetTargetMemoryInfo (AddressDomain domain)
		{
			int target_int_size, target_long_size, target_addr_size, is_bigendian;
			check_error (mono_debugger_server_get_target_info
				(out target_int_size, out target_long_size,
				 out target_addr_size, out is_bigendian));

			return new TargetMemoryInfo (target_int_size, target_long_size,
						     target_addr_size, is_bigendian != 0, domain);
		}

		public static string GetFileContents (string filename)
		{
			try {
				StreamReader sr = File.OpenText (filename);
				string contents = sr.ReadToEnd ();

				sr.Close();

				return contents;
			}
			catch {
				return null;
			}
		}

		protected void SetupInferior ()
		{
			IntPtr data = IntPtr.Zero;
			try {
				check_error (mono_debugger_server_get_signal_info (
						     server_handle, out data));

				signal_info = (SignalInfo) Marshal.PtrToStructure (
					data, typeof (SignalInfo));
				has_signals = true;
			} finally {
				g_free (data);
			}

			target_info = GetTargetMemoryInfo (address_domain);

			try {
				string cwd;
				string[] cmdline_args;

				string application = GetApplication (out cwd, out cmdline_args);

				exe = process.OperatingSystem.LoadExecutable (
					target_info, application, start.LoadNativeSymbolTable);
			} catch (Exception e) {
				if (error_handler != null)
					error_handler (this, String.Format (
							       "Can't read symbol file {0}", start.TargetApplication), e);
				else
					Console.WriteLine ("Can't read symbol file {0}: {1}",
							   start.TargetApplication, e);
				return;
			}

			arch = process.Architecture;
		}

		public BreakpointManager BreakpointManager {
			get { return breakpoint_manager; }
		}

		public NativeExecutableReader Executable {
			get { return exe; }
		}

		public TargetAddress SimpleLookup (string name)
		{
			return exe.LookupSymbol (name);
		}

		public TargetAddress GetSectionAddress (string name)
		{
			return exe.GetSectionAddress (name);
		}

		public TargetAddress EntryPoint {
			get {
				return exe.EntryPoint;
			}
		}

		public override int TargetIntegerSize {
			get {
				return target_info.TargetIntegerSize;
			}
		}

		public override int TargetLongIntegerSize {
			get {
				return target_info.TargetLongIntegerSize;
			}
		}

		public override int TargetAddressSize {
			get {
				return target_info.TargetAddressSize;
			}
		}

		public override bool IsBigEndian {
			get {
				return target_info.IsBigEndian;
			}
		}

		public override AddressDomain AddressDomain {
			get {
				return address_domain;
			}
		}

		public override TargetMemoryInfo TargetMemoryInfo {
			get {
				return target_info;
			}
		}

		IntPtr read_buffer (TargetAddress address, int size)
		{
			IntPtr data = Marshal.AllocHGlobal (size);
			TargetError result = mono_debugger_server_read_memory (
				server_handle, address.Address, size, data);
			if (result == TargetError.MemoryAccess) {
				Marshal.FreeHGlobal (data);
				throw new TargetMemoryException (address, size);
			} else if (result != TargetError.None) {
				Marshal.FreeHGlobal (data);
				throw new TargetException (result);
			}
			return data;
		}

		public override byte[] ReadBuffer (TargetAddress address, int size)
		{
			check_disposed ();
			if (size == 0)
				return new byte [0];
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, size);
				byte[] retval = new byte [size];
				Marshal.Copy (data, retval, 0, size);
				return retval;
			} finally {
				Marshal.FreeHGlobal (data);
			}
		}

		public override byte ReadByte (TargetAddress address)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, 1);
				return Marshal.ReadByte (data);
			} finally {
				Marshal.FreeHGlobal (data);
			}
		}

		public override int ReadInteger (TargetAddress address)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, 4);
				return Marshal.ReadInt32 (data);
			} finally {
				Marshal.FreeHGlobal (data);
			}
		}

		public override long ReadLongInteger (TargetAddress address)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = read_buffer (address, 8);
				return Marshal.ReadInt64 (data);
			} finally {
				Marshal.FreeHGlobal (data);
			}
		}

		public override TargetAddress ReadAddress (TargetAddress address)
		{
			check_disposed ();
			TargetAddress res;
			switch (TargetAddressSize) {
			case 4:
				res = new TargetAddress (AddressDomain, (uint) ReadInteger (address));
				break;

			case 8:
				res = new TargetAddress (AddressDomain, ReadLongInteger (address));
				break;

			default:
				throw new TargetMemoryException (
					"Unknown target address size " + TargetAddressSize);
			}

			if (res.Address == 0)
				return TargetAddress.Null;
			else
				return res;
		}

		public override string ReadString (TargetAddress address)
		{
			check_disposed ();
			StringBuilder sb = new StringBuilder ();

			while (true) {
				byte b = ReadByte (address);
				address++;

				if (b == 0)
					return sb.ToString ();

				sb.Append ((char) b);
			}
		}

		public override TargetBlob ReadMemory (TargetAddress address, int size)
		{
			check_disposed ();
			byte [] retval = ReadBuffer (address, size);
			return new TargetBlob (retval, target_info);
		}

		public override bool CanWrite {
			get {
				return true;
			}
		}

		public override void WriteBuffer (TargetAddress address, byte[] buffer)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				int size = buffer.Length;
				data = Marshal.AllocHGlobal (size);
				Marshal.Copy (buffer, 0, data, size);
				check_error (mono_debugger_server_write_memory (
					server_handle, address.Address, size, data));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
				OnMemoryChanged ();
			}
		}

		public override void WriteByte (TargetAddress address, byte value)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (1);
				Marshal.WriteByte (data, value);
				check_error (mono_debugger_server_write_memory (
					server_handle, address.Address, 1, data));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
				OnMemoryChanged ();
			}
		}

		public override void WriteInteger (TargetAddress address, int value)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (4);
				Marshal.WriteInt32 (data, value);
				check_error (mono_debugger_server_write_memory (
					server_handle, address.Address, 4, data));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
				OnMemoryChanged ();
			}
		}

		public override void WriteLongInteger (TargetAddress address, long value)
		{
			check_disposed ();
			IntPtr data = IntPtr.Zero;
			try {
				data = Marshal.AllocHGlobal (8);
				Marshal.WriteInt64 (data, value);
				check_error (mono_debugger_server_write_memory (
					server_handle, address.Address, 8, data));
			} finally {
				if (data != IntPtr.Zero)
					Marshal.FreeHGlobal (data);
				OnMemoryChanged ();
			}
		}

		public override void WriteAddress (TargetAddress address, TargetAddress value)
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

		internal override void InsertBreakpoint (BreakpointHandle handle,
							 TargetAddress address, int domain)
		{
			breakpoint_manager.InsertBreakpoint (this, handle, address, domain);
		}

		internal override void RemoveBreakpoint (BreakpointHandle handle)
		{
			breakpoint_manager.RemoveBreakpoint (this, handle);
		}

		//
		// IInferior
		//

		public event StateChangedHandler StateChanged;

		TargetState target_state = TargetState.NoTarget;
		public TargetState State {
			get {
				check_disposed ();
				return target_state;
			}
		}

		protected TargetState change_target_state (TargetState new_state)
		{
			return change_target_state (new_state, 0);
		}

		TargetState change_target_state (TargetState new_state, int arg)
		{
			if (new_state == target_state)
				return target_state;

			TargetState old_state = target_state;
			target_state = new_state;

			if (StateChanged != null)
				StateChanged (target_state, arg);

			return old_state;
		}

		public void Step ()
		{
			check_disposed ();

			TargetState old_state = change_target_state (TargetState.Running);
			try {
				check_error (mono_debugger_server_step (server_handle));
			} catch {
				change_target_state (old_state);
				throw;
			}
		}

		public void Continue ()
		{
			check_disposed ();
			TargetState old_state = change_target_state (TargetState.Running);
			try {
				check_error (mono_debugger_server_continue (server_handle));
			} catch {
				change_target_state (old_state);
				throw;
			}
		}

		public void Resume ()
		{
			check_disposed ();

			TargetState old_state = change_target_state (TargetState.Running);
			try {
				check_error (mono_debugger_server_resume (server_handle));
			} catch {
				change_target_state (old_state);
				throw;
			}
		}

		// <summary>
		//   Stop the inferior.
		//   Returns true if it actually stopped the inferior and false if it was
		//   already stopped.
		//   Note that the target may have stopped abnormally in the meantime, in
		//   this case we return the corresponding ChildEvent.
		// </summary>
		public bool Stop (out ChildEvent new_event)
		{
			check_disposed ();
			int status;
			TargetError error = mono_debugger_server_stop_and_wait (server_handle, out status);
			if (error != TargetError.None) {
				new_event = null;
				return false;
			} else if (status == 0) {
				new_event = null;
				return true;
			}

			new_event = ProcessEvent (status);
			return true;
		}

		// <summary>
		//   Just send the inferior a stop signal, but don't wait for it to stop.
		//   Returns true if it actually sent the signal and false if the target
		//   was already stopped.
		// </summary>
		public bool Stop ()
		{
			check_disposed ();
			TargetError error = mono_debugger_server_stop (server_handle);
			if(error == TargetError.AlreadyStopped)
				change_target_state (TargetState.Stopped);
			return error == TargetError.None;
		}

		public void SetSignal (int signal, bool send_it)
		{
			check_disposed ();
			int do_send = send_it ? 1 : 0;
			check_error (mono_debugger_server_set_signal (server_handle, signal, do_send));
		}

		public int GetPendingSignal ()
		{
			int signal;
			check_disposed ();
			check_error (mono_debugger_server_get_pending_signal (server_handle, out signal));
			return signal;
		}

		public void Detach ()
		{
			check_disposed ();
			if (pushed_regs)
				mono_debugger_server_pop_registers (server_handle);
			check_error (mono_debugger_server_detach (server_handle));
		}

		public void Shutdown ()
		{
			mono_debugger_server_kill (server_handle);
		}

		public void Kill ()
		{
			check_disposed ();
			check_error (mono_debugger_server_kill (server_handle));
		}

		public TargetAddress CurrentFrame {
			get {
				ServerStackFrame frame = get_current_frame ();
				return new TargetAddress (AddressDomain, frame.Address);
			}
		}

		public bool CurrentInstructionIsBreakpoint {
			get {
				check_disposed ();
				int is_breakpoint;
				TargetError result = mono_debugger_server_current_insn_is_bpt (
					server_handle, out is_breakpoint);
				if (result != TargetError.None)
					throw new TargetException (TargetError.NoStack);

				return is_breakpoint != 0;
			}
		}

		internal Architecture Architecture {
			get {
				check_disposed ();
				return arch;
			}
		}

		public Module[] Modules {
			get {
				return new Module[] { exe.Module };
			}
		}

		public override Registers GetRegisters ()
		{
			IntPtr buffer = IntPtr.Zero;
			try {
				int count = arch.CountRegisters;
				int buffer_size = count * 8;
				buffer = Marshal.AllocHGlobal (buffer_size);
				TargetError result = mono_debugger_server_get_registers (
					server_handle, buffer);
				check_error (result);
				long[] retval = new long [count];
				Marshal.Copy (buffer, retval, 0, count);

				return new Registers (arch, retval);
			} finally {
				if (buffer != IntPtr.Zero)
					Marshal.FreeHGlobal (buffer);
			}
		}

		public override void SetRegisters (Registers registers)
		{
			IntPtr buffer = IntPtr.Zero;
			try {
				int count = arch.CountRegisters;

				Registers old_regs = GetRegisters ();
				for (int i = 0; i < count; i++) {
					if (registers [i] == null)
						continue;
					if (!registers [i].Valid)
						registers [i].SetValue (old_regs [i].Value);
				}

				int buffer_size = count * 8;
				buffer = Marshal.AllocHGlobal (buffer_size);
				Marshal.Copy (registers.Values, 0, buffer, count);
				TargetError result = mono_debugger_server_set_registers (
					server_handle, buffer);
				check_error (result);
			} finally {
				if (buffer != IntPtr.Zero)
					Marshal.FreeHGlobal (buffer);
			}
		}

		public int[] GetThreads ()
		{
			IntPtr data = IntPtr.Zero;
			try {
				int count;
				check_error (mono_debugger_server_get_threads (
						     server_handle, out count, out data));

				int[] threads = new int [count];
				Marshal.Copy (data, threads, 0, count);
				return threads;
			} finally {
				g_free (data);
			}
		}

		protected string GetApplication (out string cwd, out string[] cmdline_args)
		{
			IntPtr data = IntPtr.Zero;
			IntPtr p_exe = IntPtr.Zero;
			IntPtr p_cwd = IntPtr.Zero;
			try {
				int count;
				string exe_file;
				check_error (mono_debugger_server_get_application (
						     server_handle, out p_exe, out p_cwd,
						     out count, out data));

				cmdline_args = new string [count];
				exe_file = Marshal.PtrToStringAnsi (p_exe);
				cwd = Marshal.PtrToStringAnsi (p_cwd);

				for (int i = 0; i < count; i++) {
					IntPtr ptr = Marshal.ReadIntPtr (data, i * IntPtr.Size);
					cmdline_args [i] = Marshal.PtrToStringAnsi (ptr);
				}

				return exe_file;
			} finally {
				g_free (data);
				g_free (p_exe);
				g_free (p_cwd);
			}
		}

		public void DetachAfterFork ()
		{
			mono_debugger_server_detach_after_fork (server_handle);
			Dispose ();
		}

		public TargetAddress PushRegisters ()
		{
			long new_rsp;
			check_error (mono_debugger_server_push_registers (server_handle, out new_rsp));
			pushed_regs = true;
			return new TargetAddress (AddressDomain, new_rsp);
		}

		public void PopRegisters ()
		{
			pushed_regs = false;
			check_error (mono_debugger_server_pop_registers (server_handle));
		}

		internal CallbackFrame GetCallbackFrame (TargetAddress stack_pointer, bool exact_match)
		{
			IntPtr buffer = IntPtr.Zero;
			try {
				int count = arch.CountRegisters;
				int buffer_size = 32 + count * 8;
				buffer = Marshal.AllocHGlobal (buffer_size);
				TargetError result = mono_debugger_server_get_callback_frame (
					server_handle, stack_pointer.Address, exact_match, buffer);
				if (result == TargetError.NoCallbackFrame)
					return null;
				check_error (result);

				return new CallbackFrame (this, buffer);
			} finally {
				if (buffer != IntPtr.Zero)
					Marshal.FreeHGlobal (buffer);
			}
		}

		internal class CallbackFrame
		{
			public readonly long ID;
			public readonly TargetAddress CallAddress;
			public readonly TargetAddress StackPointer;
			public readonly bool IsRuntimeInvokeFrame;
			public readonly bool IsExactMatch;
			public readonly Registers Registers;

			public CallbackFrame (Inferior inferior, IntPtr data)
			{
				ID = Marshal.ReadInt64 (data);
				CallAddress = new TargetAddress (inferior.AddressDomain, Marshal.ReadInt64 (data, 8));
				StackPointer = new TargetAddress (inferior.AddressDomain, Marshal.ReadInt64 (data, 16));

				int flags = Marshal.ReadInt32 (data, 24);
				IsRuntimeInvokeFrame = (flags & 1) == 1;
				IsExactMatch = (flags & 2) == 2;

				long[] regs = new long [inferior.arch.CountRegisters];
				for (int i = 0; i < regs.Length; i++)
					regs [i] = Marshal.ReadInt64 (data, 32 + 8 * i);

				Registers = new Registers (inferior.arch, regs);
			}

			public override string ToString ()
			{
				return String.Format ("Inferior.CallbackFrame ({0}:{1:x}:{2:x}:{3})", ID,
						      CallAddress, StackPointer, IsRuntimeInvokeFrame);
			}
		}

		internal void SetRuntimeInfo (IntPtr mono_runtime_info)
		{
			mono_debugger_server_set_runtime_info (server_handle, mono_runtime_info);
		}

		internal struct ServerStackFrame
		{
			public long Address;
			public long StackPointer;
			public long FrameAddress;
		}

		internal class StackFrame
		{
			TargetAddress address, stack, frame;

			internal StackFrame (TargetMemoryInfo info, ServerStackFrame frame)
			{
				this.address = new TargetAddress (info.AddressDomain, frame.Address);
				this.stack = new TargetAddress (info.AddressDomain, frame.StackPointer);
				this.frame = new TargetAddress (info.AddressDomain, frame.FrameAddress);
			}

			internal StackFrame (TargetAddress address, TargetAddress stack,
					     TargetAddress frame)
			{
				this.address = address;
				this.stack = stack;
				this.frame = frame;
			}

			public TargetAddress Address {
				get {
					return address;
				}
			}

			public TargetAddress StackPointer {
				get {
					return stack;
				}
			}

			public TargetAddress FrameAddress {
				get {
					return frame;
				}
			}
		}

		ServerStackFrame get_current_frame ()
		{
			check_disposed ();
			ServerStackFrame frame;
			TargetError result = mono_debugger_server_get_frame (server_handle, out frame);
			check_error (result);
			return frame;
		}

		public StackFrame GetCurrentFrame (bool may_fail)
		{
			check_disposed ();
			ServerStackFrame frame;
			TargetError result = mono_debugger_server_get_frame (server_handle, out frame);
			if (result == TargetError.None)
				return new StackFrame (target_info, frame);
			else if (may_fail)
				return null;
			else
				throw new TargetException (result);
		}

		public StackFrame GetCurrentFrame ()
		{
			return GetCurrentFrame (false);
		}

		public TargetMemoryArea[] GetMemoryMaps ()
		{
			// We cannot use System.IO to read this file because it is not
			// seekable.  Actually, the file is seekable, but it contains
			// "holes" and each line starts on a new 4096 bytes block.
			// So if you just read the first line from the file, the current
			// file position will be rounded up to the next 4096 bytes
			// boundary - it'll be different from what System.IO thinks is
			// the current file position and System.IO will try to "fix" this
			// by seeking back.
			string mapfile = String.Format ("/proc/{0}/maps", child_pid);
			string contents = GetFileContents (mapfile);

			if (contents == null)
				return null;

			ArrayList list = new ArrayList ();

			using (StringReader reader = new StringReader (contents)) {
				do {
					string l = reader.ReadLine ();
					if (l == null)
						break;

					bool is64bit;
					if (l [8] == '-')
						is64bit = false;
					else if (l [16] == '-')
						is64bit = true;
					else
						throw new InternalError ();

					string sstart = is64bit ? l.Substring (0,16) : l.Substring (0,8);
					string send = is64bit ? l.Substring (17,16) : l.Substring (9,8);
					string sflags = is64bit ? l.Substring (34,4) : l.Substring (18,4);

					long start = Int64.Parse (sstart, NumberStyles.HexNumber);
					long end = Int64.Parse (send, NumberStyles.HexNumber);

					string name;
					if (is64bit)
						name = (l.Length > 73) ? l.Substring (73) : "";
					else
						name = (l.Length > 49) ? l.Substring (49) : "";
					name = name.TrimStart (' ').TrimEnd (' ');
					if (name == "")
						name = null;

					TargetMemoryFlags flags = 0;
					if (sflags [1] != 'w')
						flags |= TargetMemoryFlags.ReadOnly;

					TargetMemoryArea area = new TargetMemoryArea (
						new TargetAddress (AddressDomain, start),
						new TargetAddress (AddressDomain, end),
						flags, name);
					list.Add (area);
				} while (true);
			}

			TargetMemoryArea[] maps = new TargetMemoryArea [list.Count];
			list.CopyTo (maps, 0);
			return maps;
		}

		protected virtual void OnMemoryChanged ()
		{
			// child_event (ChildEventType.CHILD_MEMORY_CHANGED, 0);
		}

		public bool HasSignals {
			get { return has_signals; }
		}

		public int SIGKILL {
			get {
				if (!has_signals || (signal_info.SIGKILL < 0))
					throw new InvalidOperationException ();

				return signal_info.SIGKILL;
			}
		}

		public int SIGSTOP {
			get {
				if (!has_signals || (signal_info.SIGSTOP < 0))
					throw new InvalidOperationException ();

				return signal_info.SIGSTOP;
			}
		}

		public int SIGINT {
			get {
				if (!has_signals || (signal_info.SIGINT < 0))
					throw new InvalidOperationException ();

				return signal_info.SIGINT;
			}
		}

		public int SIGCHLD {
			get {
				if (!has_signals || (signal_info.SIGCHLD < 0))
					throw new InvalidOperationException ();

				return signal_info.SIGCHLD;
			}
		}

		public bool Has_SIGWINCH {
			get { return has_signals && (signal_info.SIGWINCH > 0); }
		}

		public int SIGWINCH {
			get {
				if (!has_signals || (signal_info.SIGWINCH < 0))
					throw new InvalidOperationException ();

				return signal_info.SIGWINCH;
			}
		}

		public bool IsManagedSignal (int signal)
		{
			if (!has_signals)
				throw new InvalidOperationException ();

			if ((signal == signal_info.SIGFPE) || (signal == signal_info.SIGQUIT) ||
			    (signal == signal_info.SIGABRT) || (signal == signal_info.SIGSEGV) ||
			    (signal == signal_info.SIGILL) || (signal == signal_info.SIGBUS))
				return true;

			return false;
		}

		/*
		 * CAUTION: This is the hard limit of the Linux kernel, not the first
		 *          user-visible real-time signal !
		 */
		public int Kernel_SIGRTMIN {
			get {
				if (!has_signals || (signal_info.Kernel_SIGRTMIN < 0))
					throw new InvalidOperationException ();

				return signal_info.Kernel_SIGRTMIN;
			}
		}

		public int MonoThreadAbortSignal {
			get {
				if (!has_signals || (signal_info.MonoThreadAbortSignal < 0))
					throw new InvalidOperationException ();

				return signal_info.MonoThreadAbortSignal;
			}
		}

		public static bool HasThreadEvents {
			get {
				ServerCapabilities capabilities = mono_debugger_server_get_capabilities ();
				return (capabilities & ServerCapabilities.THREAD_EVENTS) != 0;
			}
		}

		//
		// Whether we can detach from any target.
		//
		// Background:
		//
		// The Linux kernel allows detaching from any traced child, even if we did not
		// previously attach to it.
		//

		public static bool CanDetachAny {
			get {
				ServerCapabilities capabilities = mono_debugger_server_get_capabilities ();
				return (capabilities & ServerCapabilities.CAN_DETACH_ANY) != 0;
			}
		}

		public static OperatingSystemBackend CreateOperatingSystemBackend (Process process)
		{
			ServerType type = mono_debugger_server_get_server_type ();
			switch (type) {
			case ServerType.LINUX_PTRACE:
				return new LinuxOperatingSystem (process);
			case ServerType.DARWIN:
				return new DarwinOperatingSystem (process);
			case ServerType.WINDOWS:
				return new WindowsOperatingSystem (process);
			default:
				throw new NotSupportedException (String.Format ("Unknown server type {0}.", type));
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
				this.disposed = true;

				// Release unmanaged resources
				lock (this) {
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
