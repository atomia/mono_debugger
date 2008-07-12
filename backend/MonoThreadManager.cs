using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Configuration;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.InteropServices;

using Mono.Debugger;
using Mono.Debugger.Languages;
using Mono.Debugger.Languages.Mono;


namespace Mono.Debugger.Backend
{

// <summary>
// MonoThreadManager is a special case handler for thread events when
// we know we're running a managed app.
// </summary>

	internal enum NotificationType {
		InitializeManagedCode	= 1,
		InitializeCorlib,
		JitBreakpoint,
		InitializeThreadManager,
		AcquireGlobalThreadLock,
		ReleaseGlobalThreadLock,
		WrapperMain,
		MainExited,
		UnhandledException,
		ThrowException,
		HandleException,
		ThreadCreated,
		ThreadCleanup,
		GcThreadCreated,
		GcThreadExited,
		ReachedMain,
		FinalizeManagedCode,
		LoadModule,
		UnloadModule,
		DomainCreate,
		DomainUnload,
		ClassInitialized,
		InterruptionRequest,

		Trampoline	= 256
	}

	internal delegate object ManagedCallbackFunction (SingleSteppingEngine engine);

	internal class MonoThreadManager
	{
		MonoDebuggerInfo debugger_info;

		public static MonoThreadManager Initialize (ThreadManager thread_manager,
							    Inferior inferior, bool attach)
		{
			TargetAddress info = inferior.GetSectionAddress (".mdb_debug_info");
			if (!info.IsNull)
				info = inferior.ReadAddress (info);
			else
				info = inferior.SimpleLookup ("MONO_DEBUGGER__debugger_info");
			if (info.IsNull)
				return null;

			return new MonoThreadManager (thread_manager, inferior, info, attach);
		}

		protected MonoThreadManager (ThreadManager thread_manager, Inferior inferior,
					     TargetAddress info, bool attach)
		{
			debugger_info = MonoDebuggerInfo.Create (inferior, info);

			inferior.WriteInteger (debugger_info.UsingMonoDebugger, 1);

			notification_bpt = new InitializeBreakpoint (this, debugger_info.Initialize);
			notification_bpt.Insert (inferior);
		}

		AddressBreakpoint notification_bpt;
		IntPtr mono_runtime_info;

		internal bool HasCodeBuffer {
			get;
			private set;
		}

		[DllImport("monodebuggerserver")]
		static extern IntPtr mono_debugger_server_initialize_mono_runtime (
			int address_size, long notification_address,
			long executable_code_buffer, int executable_code_buffer_size,
			long breakpoint_info, long breakpoint_info_index,
			int breakpoint_table_size);

		[DllImport("monodebuggerserver")]
		static extern void mono_debugger_server_finalize_mono_runtime (IntPtr handle);

		[DllImport("monodebuggerserver")]
		static extern void mono_debugger_server_initialize_code_buffer (
			IntPtr runtime, long executable_code_buffer,
			int executable_code_buffer_size);

		protected void initialize_notifications (Inferior inferior)
		{
			TargetAddress executable_code_buffer = inferior.ReadAddress (
				debugger_info.ExecutableCodeBuffer);
			HasCodeBuffer = !executable_code_buffer.IsNull;

			mono_runtime_info = mono_debugger_server_initialize_mono_runtime (
				inferior.TargetAddressSize,
				debugger_info.NotificationAddress.Address,
				executable_code_buffer.Address,
				debugger_info.ExecutableCodeBufferSize,
				debugger_info.BreakpointInfo.Address,
				debugger_info.BreakpointInfoIndex.Address,
				debugger_info.BreakpointArraySize);
			inferior.SetRuntimeInfo (mono_runtime_info);

			inferior.WriteInteger (debugger_info.DebuggerVersion, 4);

			if (notification_bpt != null) {
				notification_bpt.Remove (inferior);
				notification_bpt = null;
			}
		}

		internal void InitCodeBuffer (Inferior inferior, TargetAddress code_buffer)
		{
			HasCodeBuffer = true;
			mono_debugger_server_initialize_code_buffer (
				mono_runtime_info, code_buffer.Address,
				debugger_info.ExecutableCodeBufferSize);
		}

		protected class InitializeBreakpoint : AddressBreakpoint
		{
			protected readonly MonoThreadManager manager;

			public InitializeBreakpoint (MonoThreadManager manager, TargetAddress address)
				: base ("initialize", ThreadGroup.System, address)
			{
				this.manager = manager;
			}

			public override bool CheckBreakpointHit (Thread target, TargetAddress address)
			{
				return true;
			}

			internal override bool BreakpointHandler (Inferior inferior,
								  out bool remain_stopped)
			{
				manager.initialize_notifications (inferior);
				remain_stopped = false;
				return true;
			}
		}

		internal void InitializeAfterAttach (Inferior inferior)
		{
			initialize_notifications (inferior);

			inferior.WriteAddress (debugger_info.ThreadVTablePtr,
					       debugger_info.ThreadVTable);
			inferior.WriteAddress (debugger_info.EventHandlerPtr,
					       debugger_info.EventHandler);
			inferior.WriteInteger (debugger_info.UsingMonoDebugger, 1);

			csharp_language = inferior.Process.CreateMonoLanguage (debugger_info);
			csharp_language.InitializeAttach (inferior);
			inferior.InitializeModules ();
		}

		internal void InitializeAfterExec (Inferior inferior)
		{
			inferior.WriteInteger (debugger_info.UsingMonoDebugger, 1);
		}

		internal void Detach (Inferior inferior)
		{
			inferior.WriteAddress (debugger_info.ThreadVTablePtr, TargetAddress.Null);
			inferior.WriteAddress (debugger_info.EventHandler, TargetAddress.Null);
			inferior.WriteInteger (debugger_info.UsingMonoDebugger, 0);
		}

		internal void AddManagedCallback (Inferior inferior, ManagedCallbackFunction func)
		{
			inferior.WriteInteger (MonoDebuggerInfo.InterruptionRequest, 1);
			managed_callbacks.Enqueue (func);
		}

		TargetAddress main_function;
		TargetAddress main_thread;
		MonoLanguageBackend csharp_language;
		Queue<ManagedCallbackFunction> managed_callbacks = new Queue<ManagedCallbackFunction> ();

		internal bool CanExecuteCode {
			get { return mono_runtime_info != IntPtr.Zero; }
		}

		internal MonoDebuggerInfo MonoDebuggerInfo {
			get { return debugger_info; }
		}

		int index;
		internal void ThreadCreated (SingleSteppingEngine sse)
		{
			sse.Inferior.SetRuntimeInfo (mono_runtime_info);

			if (++index < 3)
				sse.SetDaemon ();
		}

		internal bool HandleChildEvent (SingleSteppingEngine engine, Inferior inferior,
						ref Inferior.ChildEvent cevent, out bool resume_target)
		{
			if (cevent.Type == Inferior.ChildEventType.CHILD_NOTIFICATION) {
				NotificationType type = (NotificationType) cevent.Argument;

				Report.Debug (DebugFlags.EventLoop,
					      "{0} received notification {1}: {2}",
					      engine, type, cevent);

				switch (type) {
				case NotificationType.AcquireGlobalThreadLock:
					Report.Debug (DebugFlags.Threads,
						      "{0} received notification {1}", engine, type);
					engine.ProcessServant.AcquireGlobalThreadLock (engine);
					break;

				case NotificationType.ReleaseGlobalThreadLock:
					Report.Debug (DebugFlags.Threads,
						      "{0} received notification {1}", engine, type);
					engine.ProcessServant.ReleaseGlobalThreadLock (engine);
					break;

				case NotificationType.ThreadCreated: {
					TargetAddress data = new TargetAddress (
						inferior.AddressDomain, cevent.Data2);

					TargetAddress lmf = inferior.ReadAddress (data + 8);
					engine.SetManagedThreadData (lmf, data + 24);

					Report.Debug (DebugFlags.Threads,
						      "{0} managed thread created: {1:x} {2} {3} - {4}",
						      engine, cevent.Data1, data, lmf, engine.LMFAddress);
					break;
				}

				case NotificationType.ThreadCleanup: {
					TargetAddress data = new TargetAddress (
						inferior.AddressDomain, cevent.Data1);

					Report.Debug (DebugFlags.Threads,
						      "{0} managed thread cleanup: {1:x} {2}",
						      engine, cevent.Data2, data);
					break;
				}

				case NotificationType.GcThreadCreated: {
					TargetAddress data = new TargetAddress (
						inferior.AddressDomain, cevent.Data1);
					long tid = cevent.Data2;

					Report.Debug (DebugFlags.Threads,
						      "{0} created gc thread: {1:x} {2}",
						      engine, tid, data);

					engine = engine.ProcessServant.GetEngineByTID (inferior, tid);
					if (engine == null)
						throw new InternalError ();

					engine.OnManagedThreadCreated (data);
					break;
				}

				case NotificationType.GcThreadExited:
					engine.OnManagedThreadExited ();
					break;

				case NotificationType.InitializeThreadManager:
					csharp_language = inferior.Process.CreateMonoLanguage (
						debugger_info);
					if (engine.ProcessServant.IsAttached)
						csharp_language.InitializeAttach (inferior);
					else
						csharp_language.Initialize (inferior);

					inferior.InitializeModules ();
					if (!engine.ProcessServant.IsAttached)
						engine.ProcessServant.InitializeThreads (inferior);

					break;

				case NotificationType.ReachedMain: {
					Inferior.StackFrame iframe = inferior.GetCurrentFrame (false);
					engine.SetMainReturnAddress (iframe.StackPointer);
					engine.ProcessServant.OnProcessReachedMainEvent ();
					resume_target = !engine.OnModuleLoaded ();
					return true;
				}

				case NotificationType.WrapperMain:
				case NotificationType.MainExited:
					break;

				case NotificationType.UnhandledException:
					cevent = new Inferior.ChildEvent (
						Inferior.ChildEventType.UNHANDLED_EXCEPTION,
						0, cevent.Data1, cevent.Data2);
					resume_target = false;
					return false;

				case NotificationType.HandleException:
					cevent = new Inferior.ChildEvent (
						Inferior.ChildEventType.HANDLE_EXCEPTION,
						0, cevent.Data1, cevent.Data2);
					resume_target = false;
					return false;

				case NotificationType.ThrowException:
					cevent = new Inferior.ChildEvent (
						Inferior.ChildEventType.THROW_EXCEPTION,
						0, cevent.Data1, cevent.Data2);
					resume_target = false;
					return false;

				case NotificationType.FinalizeManagedCode:
					mono_debugger_server_finalize_mono_runtime (mono_runtime_info);
					mono_runtime_info = IntPtr.Zero;
					csharp_language = null;
					break;

				case NotificationType.Trampoline:
					resume_target = false;
					return false;

				case NotificationType.ClassInitialized:
					break;

#if MARTIN_PRIVATE
				case NotificationType.InterruptionRequest:
					inferior.WriteInteger (MonoDebuggerInfo.InterruptionRequest, 0);
					engine.DoManagedCallback (managed_callbacks.ToArray ());
					managed_callbacks = new Queue<ManagedCallbackFunction> ();
					resume_target = false;
					return true;
#endif

				default: {
					TargetAddress data = new TargetAddress (
						inferior.AddressDomain, cevent.Data1);

					resume_target = csharp_language.Notification (
						engine, inferior, type, data, cevent.Data2);
					return true;
				}
				}

				resume_target = true;
				return true;
			}

			if ((cevent.Type == Inferior.ChildEventType.CHILD_STOPPED) &&
			    (cevent.Argument == inferior.MonoThreadAbortSignal)) {
				resume_target = true;
				return true;
			}

			resume_target = false;
			return false;
		}
	}

	// <summary>
	//   This class is the managed representation of the MONO_DEBUGGER__debugger_info struct.
	//   as defined in mono/mini/debug-debugger.h
	// </summary>
	internal class MonoDebuggerInfo
	{
		// These constants must match up with those in mono/mono/metadata/mono-debug.h
		public const int  MinDynamicVersion = 80;
		public const int  MaxDynamicVersion = 80;
		public const long DynamicMagic      = 0x7aff65af4253d427;

		public readonly int MonoTrampolineNum;
		public readonly TargetAddress MonoTrampolineCode;
		public readonly TargetAddress NotificationAddress;
		public readonly TargetAddress SymbolTable;
		public readonly int SymbolTableSize;
		public readonly TargetAddress MonoMetadataInfo;
		public readonly TargetAddress DebuggerVersion;
		public readonly TargetAddress CompileMethod;
		public readonly TargetAddress GetVirtualMethod;
		public readonly TargetAddress GetBoxedObjectMethod;
		public readonly TargetAddress RuntimeInvoke;
		public readonly TargetAddress CreateString;
		public readonly TargetAddress ClassGetStaticFieldData;
		public readonly TargetAddress LookupClass;
		public readonly TargetAddress RunFinally;
		public readonly TargetAddress InsertMethodBreakpoint;
		public readonly TargetAddress InsertSourceBreakpoint;
		public readonly TargetAddress RemoveBreakpoint;
		public readonly TargetAddress RegisterClassInitCallback;
		public readonly TargetAddress RemoveClassInitCallback;
		public readonly TargetAddress Initialize;
		public readonly TargetAddress ThreadTable;
		public readonly TargetAddress ExecutableCodeBuffer;
		public readonly TargetAddress BreakpointInfo;
		public readonly TargetAddress BreakpointInfoIndex;
		public readonly int ExecutableCodeBufferSize;
		public readonly int BreakpointArraySize;
		public readonly TargetAddress GetMethodSignature;
		public readonly TargetAddress InitCodeBuffer;

		public readonly TargetAddress ThreadVTablePtr;
		public readonly TargetAddress ThreadVTable;
		public readonly TargetAddress EventHandlerPtr;
		public readonly TargetAddress EventHandler;

		public readonly TargetAddress UsingMonoDebugger;
		public readonly TargetAddress InterruptionRequest;

		public static MonoDebuggerInfo Create (TargetMemoryAccess memory, TargetAddress info)
		{
			TargetBinaryReader header = memory.ReadMemory (info, 24).GetReader ();
			long magic = header.ReadInt64 ();
			if (magic != DynamicMagic)
				throw new SymbolTableException (
					"`MONO_DEBUGGER__debugger_info' has unknown magic {0:x}.", magic);

			int version = header.ReadInt32 ();
			if (version < MinDynamicVersion)
				throw new SymbolTableException (
					"`MONO_DEBUGGER__debugger_info' has version {0}, " +
					"but expected at least {1}.", version,
					MonoDebuggerInfo.MinDynamicVersion);
			if (version > MaxDynamicVersion)
				throw new SymbolTableException (
					"`MONO_DEBUGGER__debugger_info' has version {0}, " +
					"but expected at most {1}.", version,
					MonoDebuggerInfo.MaxDynamicVersion);

			int minor_version = header.ReadInt32 ();
			header.ReadInt32 ();

			int size = header.ReadInt32 ();

			TargetReader reader = new TargetReader (memory.ReadMemory (info, size));
			return new MonoDebuggerInfo (memory, reader);
		}

		protected MonoDebuggerInfo (TargetMemoryAccess memory, TargetReader reader)
		{
			/* skip past magic, version, and total_size */
			reader.Offset = 24;

			SymbolTableSize           = reader.ReadInteger ();
			MonoTrampolineNum         = reader.ReadInteger ();
			MonoTrampolineCode        = reader.ReadAddress ();
			NotificationAddress       = reader.ReadAddress ();
			SymbolTable               = reader.ReadAddress ();
			MonoMetadataInfo          = reader.ReadAddress ();
			DebuggerVersion           = reader.ReadAddress ();

			CompileMethod             = reader.ReadAddress ();
			GetVirtualMethod          = reader.ReadAddress ();
			GetBoxedObjectMethod      = reader.ReadAddress ();
			RuntimeInvoke             = reader.ReadAddress ();
			ClassGetStaticFieldData   = reader.ReadAddress ();
			RunFinally                = reader.ReadAddress ();
			Initialize                = reader.ReadAddress ();

			CreateString              = reader.ReadAddress ();
			LookupClass               = reader.ReadAddress ();

			InsertMethodBreakpoint    = reader.ReadAddress ();
			InsertSourceBreakpoint    = reader.ReadAddress ();
			RemoveBreakpoint          = reader.ReadAddress ();

			RegisterClassInitCallback = reader.ReadAddress ();
			RemoveClassInitCallback   = reader.ReadAddress ();

			ThreadTable               = reader.ReadAddress ();

			ExecutableCodeBuffer      = reader.ReadAddress ();
			BreakpointInfo            = reader.ReadAddress ();
			BreakpointInfoIndex       = reader.ReadAddress ();

			ExecutableCodeBufferSize  = reader.ReadInteger ();
			BreakpointArraySize       = reader.ReadInteger ();

			GetMethodSignature        = reader.ReadAddress ();
			InitCodeBuffer            = reader.ReadAddress ();

			ThreadVTablePtr           = reader.ReadAddress ();
			ThreadVTable              = reader.ReadAddress ();
			EventHandlerPtr           = reader.ReadAddress ();
			EventHandler              = reader.ReadAddress ();

			UsingMonoDebugger         = reader.ReadAddress ();
			InterruptionRequest       = reader.ReadAddress ();

			Report.Debug (DebugFlags.JitSymtab, this);
		}
	}
}
