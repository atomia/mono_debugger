#include <config.h>
#include <mono/io-layer/io-layer.h>
#include <mono/metadata/threads.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/mono-debug.h>
#include <mono/metadata/mono-config.h>
#define _IN_THE_MONO_DEBUGGER
#include "debug-debugger.h"
#include "debug-mini.h"
#include <libgc/include/libgc-mono-debugger.h>
#include "mini.h"
#include <unistd.h>
#include <locale.h>
#include <string.h>

/*
 * This file is only compiled on platforms where the debugger is supported - see the conditional
 * definition of `debugger_sources' in Makefile.am.
 *
 * configure.in checks whether we're using the included libgc and disables the debugger if not.
 */

#if !defined(MONO_DEBUGGER_SUPPORTED)
#error "Some clown tried to compile debug-debugger.c on an unsupported platform - fix Makefile.am!"
#elif !defined(USE_INCLUDED_LIBGC)
#error "Some clown #defined MONO_DEBUGGER_SUPPORTED without USE_INCLUDED_GC - fix configure.in!"
#endif

static guint64 debugger_insert_breakpoint (guint64 index, G_GNUC_UNUSED guint64 dummy_argument,
					   const gchar *string_argument);
static guint64 debugger_remove_breakpoint (guint64 breakpoint);
static guint64 debugger_register_class_init_callback (guint64 index, G_GNUC_UNUSED guint64 dummy,
						      const gchar *string_argument);
static guint64 debugger_remove_class_init_callback (guint64 index);
static guint64 debugger_compile_method (guint64 method_arg);
static guint64 debugger_get_virtual_method (guint64 class_arg, guint64 method_arg);
static guint64 debugger_get_boxed_object (guint64 klass_arg, guint64 val_arg);
static guint64 debugger_create_string (G_GNUC_UNUSED guint64 dummy, G_GNUC_UNUSED guint64 dummy2,
				       const gchar *string_argument);
static guint64 debugger_class_get_static_field_data (guint64 klass);
static gint64 debugger_lookup_class (guint64 image_argument, G_GNUC_UNUSED guint64 dummy,
				     gchar *full_name);
static guint64 debugger_lookup_assembly (G_GNUC_UNUSED guint64 dummy, G_GNUC_UNUSED guint64 dummy2,
					 const gchar *string_argument);
static guint64 debugger_run_finally (guint64 argument1, guint64 argument2);
static guint64 debugger_get_current_thread (void);
static gint64 debugger_get_method_addr_or_bpt (guint64 method_argument, guint64 index);
static void debugger_remove_method_breakpoint (G_GNUC_UNUSED guint64 dummy, guint64 index);
static void debugger_attach (void);
static void debugger_detach (void);
static void debugger_initialize (void);

static void (*mono_debugger_notification_function) (guint64 command, guint64 data, guint64 data2);

static MonoDebuggerMetadataInfo debugger_metadata_info = {
	sizeof (MonoDebuggerMetadataInfo),
	sizeof (MonoDefaults),
	&mono_defaults,
	sizeof (MonoType),
	sizeof (MonoArrayType),
	sizeof (MonoClass),
	sizeof (MonoThread),
	G_STRUCT_OFFSET (MonoThread, tid),
	G_STRUCT_OFFSET (MonoThread, stack_ptr),
	G_STRUCT_OFFSET (MonoThread, end_stack),
	G_STRUCT_OFFSET (MonoClass, instance_size),
	G_STRUCT_OFFSET (MonoClass, parent),
	G_STRUCT_OFFSET (MonoClass, type_token),
	G_STRUCT_OFFSET (MonoClass, fields),
	G_STRUCT_OFFSET (MonoClass, methods),
	G_STRUCT_OFFSET (MonoClass, method.count),
	G_STRUCT_OFFSET (MonoClass, this_arg),
	G_STRUCT_OFFSET (MonoClass, byval_arg),
	G_STRUCT_OFFSET (MonoClass, generic_class),
	G_STRUCT_OFFSET (MonoClass, generic_container),
	sizeof (MonoClassField),
	G_STRUCT_OFFSET (MonoDefaults, corlib),
	G_STRUCT_OFFSET (MonoDefaults, object_class),
	G_STRUCT_OFFSET (MonoDefaults, byte_class),
	G_STRUCT_OFFSET (MonoDefaults, void_class),
	G_STRUCT_OFFSET (MonoDefaults, boolean_class),
	G_STRUCT_OFFSET (MonoDefaults, sbyte_class),
	G_STRUCT_OFFSET (MonoDefaults, int16_class),
	G_STRUCT_OFFSET (MonoDefaults, uint16_class),
	G_STRUCT_OFFSET (MonoDefaults, int32_class),
	G_STRUCT_OFFSET (MonoDefaults, uint32_class),
	G_STRUCT_OFFSET (MonoDefaults, int_class),
	G_STRUCT_OFFSET (MonoDefaults, uint_class),
	G_STRUCT_OFFSET (MonoDefaults, int64_class),
	G_STRUCT_OFFSET (MonoDefaults, uint64_class),
	G_STRUCT_OFFSET (MonoDefaults, single_class),
	G_STRUCT_OFFSET (MonoDefaults, double_class),
	G_STRUCT_OFFSET (MonoDefaults, char_class),
	G_STRUCT_OFFSET (MonoDefaults, string_class),
	G_STRUCT_OFFSET (MonoDefaults, enum_class),
	G_STRUCT_OFFSET (MonoDefaults, array_class),
	G_STRUCT_OFFSET (MonoDefaults, delegate_class),
	G_STRUCT_OFFSET (MonoDefaults, exception_class)
};

/*
 * This is a global data symbol which is read by the debugger.
 */
MonoDebuggerInfo MONO_DEBUGGER__debugger_info = {
	MONO_DEBUGGER_MAGIC,
	MONO_DEBUGGER_VERSION,
	sizeof (MonoDebuggerInfo),
	sizeof (MonoSymbolTable),
	0,
	&mono_debugger_notification_function,
	mono_trampoline_code,
	&mono_symbol_table,
	&debugger_metadata_info,
	&debugger_compile_method,
	&debugger_get_virtual_method,
	&debugger_get_boxed_object,
	&debugger_insert_breakpoint,
	&debugger_remove_breakpoint,
	&debugger_register_class_init_callback,
	&debugger_remove_class_init_callback,
	&mono_debugger_runtime_invoke,
	&debugger_create_string,
	&debugger_class_get_static_field_data,
	&debugger_lookup_class,
	&debugger_lookup_assembly,
	&debugger_run_finally,
	&debugger_get_current_thread,
	&debugger_get_method_addr_or_bpt,
	&debugger_remove_method_breakpoint,
	&debugger_attach,
	&debugger_detach,
	&debugger_initialize,
	(void*)&mono_get_lmf_addr
};

static guint64
debugger_insert_breakpoint (guint64 index, G_GNUC_UNUSED guint64 dummy_argument,
			    const gchar *string_argument)
{
	MonoMethodDesc *desc;

	desc = mono_method_desc_new (string_argument, TRUE);
	if (!desc)
		return 0;

	return (guint64) mono_debugger_insert_breakpoint_full (desc);
}

static guint64
debugger_remove_breakpoint (guint64 breakpoint)
{
	return mono_debugger_remove_breakpoint (breakpoint);
}

static guint64
debugger_register_class_init_callback (guint64 image_arg, guint64 index,
				       const gchar *string_argument)
{
	MonoImage *image = (MonoImage *) GUINT_TO_POINTER ((gsize) image_arg);

	return (guint64) (gsize) mono_debugger_register_class_init_callback (
		image, index, string_argument);
}

static guint64
debugger_remove_class_init_callback (guint64 index)
{
	mono_debugger_remove_class_init_callback (index);
	return 0;
}

static gpointer
debugger_compile_method_cb (MonoMethod *method)
{
	gpointer retval;

	mono_debugger_lock ();
	retval = mono_compile_method (method);
	mono_debugger_unlock ();

	mono_debugger_notification_function (
		MONO_DEBUGGER_EVENT_METHOD_COMPILED, (guint64) (gsize) retval, 0);

	return retval;
}

static guint64
debugger_compile_method (guint64 method_arg)
{
	MonoMethod *method = (MonoMethod *) GUINT_TO_POINTER ((gsize) method_arg);

	return (guint64) (gsize) debugger_compile_method_cb (method);
}

static guint64
debugger_get_virtual_method (guint64 object_arg, guint64 method_arg)
{
	MonoObject *object = (MonoObject *) GUINT_TO_POINTER ((gsize) object_arg);
	MonoMethod *method = (MonoMethod *) GUINT_TO_POINTER ((gsize) method_arg);

	if (mono_class_is_valuetype (mono_method_get_class (method)))
		return method_arg;

	return (guint64) (gsize) mono_object_get_virtual_method (object, method);
}

static guint64
debugger_get_boxed_object (guint64 klass_arg, guint64 val_arg)
{
	static MonoObject *last_boxed_object = NULL;
	MonoClass *klass = (MonoClass *) GUINT_TO_POINTER ((gsize) klass_arg);
	gpointer val = (gpointer) GUINT_TO_POINTER ((gsize) val_arg);
	MonoObject *boxed;

	if (!mono_class_is_valuetype (klass))
		return val_arg;

	boxed = mono_value_box (mono_domain_get (), klass, val);
	last_boxed_object = boxed; // Protect the object from being garbage collected

	return (guint64) (gsize) boxed;
}

static guint64
debugger_create_string (G_GNUC_UNUSED guint64 dummy, G_GNUC_UNUSED guint64 dummy2,
			const gchar *string_argument)
{
	return (guint64) (gsize) mono_string_new_wrapper (string_argument);
}

static gint64
debugger_lookup_class (guint64 image_argument, G_GNUC_UNUSED guint64 dummy,
		       gchar *full_name)
{
	MonoImage *image = (MonoImage *) GUINT_TO_POINTER ((gsize) image_argument);
	gchar *name_space, *name, *pos;
	MonoClass *klass;

	g_message (G_STRLOC ": %p - %p", image, full_name);
	g_message (G_STRLOC ": %p - %s", image, full_name);
	pos = strrchr (full_name, '.');
	if (pos) {
		name_space = full_name;
		*pos = 0;
		name = pos + 1;
	} else {
		name = full_name;
		name_space = NULL;
	}

	klass = mono_class_from_name (image, name_space ? name_space : "", name);
	g_message (G_STRLOC ": %p - %s - %p", image, full_name, klass);
	if (!klass)
		return -1;

	mono_class_init (klass);
	return (gint64) (gssize) klass;
}

static guint64
debugger_lookup_assembly (G_GNUC_UNUSED guint64 dummy, G_GNUC_UNUSED guint64 dummy2,
			  const gchar *string_argument)
{
	gint64 retval;

	mono_debugger_lock ();
	retval = mono_debugger_lookup_assembly (string_argument);
	mono_debugger_unlock ();
	return retval;
}

static guint64
debugger_run_finally (guint64 context_argument, G_GNUC_UNUSED guint64 dummy)
{
	mono_debugger_run_finally (GUINT_TO_POINTER ((gsize)context_argument));
	return 0;
}

static guint64
debugger_class_get_static_field_data (guint64 value)
{
	MonoClass *klass = GUINT_TO_POINTER ((gsize) value);
	MonoVTable *vtable = mono_class_vtable (mono_domain_get (), klass);
	return (guint64) (gsize) mono_vtable_get_static_field_data (vtable);
}

static gint64
debugger_get_method_addr_or_bpt (guint64 method_argument, guint64 index)
{
	MonoMethod *method = GUINT_TO_POINTER ((gsize) method_argument);
	MonoDomain *domain = mono_get_root_domain ();
	MonoJitInfo *info;

	g_message (G_STRLOC ": %p - %p - %s.%s.%s", method, domain,
		   method->klass->name_space, method->klass->name, method->name);

	mono_domain_lock (domain);

	if (method->iflags & METHOD_IMPL_ATTRIBUTE_RUNTIME) {
		const char *name = method->name;
		MonoMethod *nm = NULL;

		if (method->klass->parent == mono_defaults.multicastdelegate_class) {
			if (*name == 'I' && (strcmp (name, "Invoke") == 0))
			        nm = mono_marshal_get_delegate_invoke (method);
			else if (*name == 'B' && (strcmp (name, "BeginInvoke") == 0))
				nm = mono_marshal_get_delegate_begin_invoke (method);
			else if (*name == 'E' && (strcmp (name, "EndInvoke") == 0))
				nm = mono_marshal_get_delegate_end_invoke (method);
		}

		if (!nm) {
			mono_domain_unlock (domain);
			return -1;
		}

		method = nm;
	}

	if ((info = g_hash_table_lookup (domain->jit_code_hash, method))) {
		mono_domain_unlock (domain);
		g_message (G_STRLOC ": %p - %p", method, info->code_start);
		return (gint64) (gssize) info->code_start;
	}

	g_message (G_STRLOC ": %p - %Ld", method, index);
	mono_debugger_insert_method_breakpoint (method, index);
	mono_domain_unlock (domain);
	return 0;
}

static void
debugger_remove_method_breakpoint (G_GNUC_UNUSED guint64 dummy, guint64 index)
{
	mono_debugger_lock ();
	g_message (G_STRLOC ": %Ld", index);
	mono_debugger_remove_method_breakpoint (index);
	mono_debugger_unlock ();
}

static void
debugger_event_handler (MonoDebuggerEvent event, guint64 data, guint64 arg)
{
	mono_debugger_notification_function (event, data, arg);
}

static guint64
debugger_get_current_thread (void)
{
	return (guint64) (gsize) mono_thread_current ();
}

static void
debugger_gc_thread_created (pthread_t thread, void *stack_ptr)
{
	mono_debugger_event (MONO_DEBUGGER_EVENT_THREAD_CREATED,
			     (guint64) (gsize) stack_ptr, thread);
}

static void
debugger_gc_thread_exited (pthread_t thread, void *stack_ptr)
{
	mono_debugger_event (MONO_DEBUGGER_EVENT_THREAD_EXITED,
			     (guint64) (gsize) stack_ptr, thread);
}

static void
debugger_gc_stop_world (void)
{
	mono_debugger_event (
		MONO_DEBUGGER_EVENT_ACQUIRE_GLOBAL_THREAD_LOCK, 0, 0);
}

static void
debugger_gc_start_world (void)
{
	mono_debugger_event (
		MONO_DEBUGGER_EVENT_RELEASE_GLOBAL_THREAD_LOCK, 0, 0);
}

static GCThreadFunctions debugger_thread_vtable = {
	NULL,

	debugger_gc_thread_created,
	debugger_gc_thread_exited,

	debugger_gc_stop_world,
	debugger_gc_start_world
};

static void
debugger_init_threads (void)
{
	gc_thread_vtable = &debugger_thread_vtable;
}

static void
debugger_finalize_threads (void)
{
	gc_thread_vtable = NULL;
}

static void
debugger_attach (void)
{
	mono_debugger_init ();

	mono_debugger_event_handler = debugger_event_handler;
	mono_debugger_notification_function (MONO_DEBUGGER_EVENT_INITIALIZE_MANAGED_CODE, 0, 0);

	debugger_init_threads ();
	GC_mono_debugger_add_all_threads ();
}

static void
debugger_detach (void)
{
	mono_debugger_event_handler = NULL;
	mono_debugger_notification_function = NULL;
	debugger_finalize_threads ();
}

extern MonoDebuggerInfo *MONO_DEBUGGER__debugger_info_ptr;

static void
debugger_initialize (void)
{
}

void
mono_debugger_init (void)
{
	mono_debugger_notification_function = mono_debugger_create_notification_function ();
	mono_debugger_event_handler = debugger_event_handler;

	/*
	 * Use an indirect call so gcc can't optimize it away.
	 */
	MONO_DEBUGGER__debugger_info.initialize ();

	debugger_init_threads ();

	/*
	 * Initialize the thread manager.
	 */

	mono_debugger_notification_function (MONO_DEBUGGER_EVENT_INITIALIZE_THREAD_MANAGER,
					     GetCurrentThreadId (), 0);
}

typedef struct 
{
	MonoDomain *domain;
	const char *file;
} DebuggerThreadArgs;

typedef struct
{
	MonoDomain *domain;
	MonoMethod *method;
	int argc;
	char **argv;
} MainThreadArgs;

static guint32
main_thread_handler (gpointer user_data)
{
	MainThreadArgs *main_args = (MainThreadArgs *) user_data;
	int retval;

	mono_debugger_notification_function (MONO_DEBUGGER_EVENT_REACHED_MAIN,
					     (guint64) (gsize) main_args->method, 0);

	retval = mono_runtime_run_main (main_args->method, main_args->argc, main_args->argv, NULL);

	/*
	 * This will never return.
	 */
	mono_debugger_notification_function (MONO_DEBUGGER_EVENT_MAIN_EXITED, 0,
					     (guint64) (gsize) retval);

	return retval;
}

int
mono_debugger_main (MonoDomain *domain, MonoAssembly *assembly, int argc, char **argv)
{
	MainThreadArgs main_args;
	MonoImage *image;
	MonoMethod *main_method;

	/*
	 * Get and compile the main function.
	 */

	image = mono_assembly_get_image (assembly);
	main_method = mono_get_method (image, mono_image_get_entry_point (image), NULL);

	/*
	 * Reload symbol tables.
	 *
	 * NOTE: We only reference the `MONO_DEBUGGER__debugger_info_ptr' here to prevent the
	 * linker from removing the .mdb_debug_info section.
	 */
	mono_debugger_notification_function (MONO_DEBUGGER_EVENT_INITIALIZE_MANAGED_CODE,
					     (guint64) (gssize) MONO_DEBUGGER__debugger_info_ptr, 0);
	mono_debugger_unlock ();

	/*
	 * Start the main thread and wait until it's ready.
	 */

	main_args.domain = domain;
	main_args.method = main_method;
	main_args.argc = argc;
	main_args.argv = argv;

#if RUN_IN_SUBTHREAD
	mono_thread_create (domain, main_thread_handler, &main_args);
#else
	main_thread_handler (&main_args);
#endif

	mono_thread_manage ();

	/*
	 * This will never return.
	 */
	mono_debugger_notification_function (MONO_DEBUGGER_EVENT_WRAPPER_MAIN, 0, 0);

	return 0;
}
