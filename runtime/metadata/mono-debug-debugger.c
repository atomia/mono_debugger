#include <config.h>
#include <stdlib.h>
#include <string.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/metadata.h>
#include <mono/metadata/tabledefs.h>
#include <mono/metadata/tokentype.h>
#include <mono/metadata/appdomain.h>
#include <mono/metadata/gc-internal.h>
#include <mono/metadata/threads.h>
#include <mono/os/gc_wrapper.h>
#include <mono/metadata/object-internals.h>
#include <mono/metadata/class-internals.h>
#include <mono/metadata/exception.h>
#include <mono/metadata/mono-debug.h>
#include <mono/metadata/mono-debug-debugger.h>
#include <mono/metadata/mono-endian.h>

static guint32 debugger_lock_level = 0;
static CRITICAL_SECTION debugger_lock_mutex;
static gboolean must_reload_symtabs = FALSE;
static gboolean mono_debugger_use_debugger = FALSE;
static MonoObject *last_exception = NULL;

void (*mono_debugger_event_handler) (MonoDebuggerEvent event, guint64 data, guint64 arg) = NULL;

#define WRITE_UINT32(ptr,value) G_STMT_START {	\
	* ((guint32 *) ptr) = value;		\
	ptr += 4;				\
} G_STMT_END

#define WRITE_POINTER(ptr,value) G_STMT_START {	\
	* ((gpointer *) ptr) = (gpointer) (value); \
	ptr += sizeof (gpointer);		\
} G_STMT_END

#define WRITE_STRING(ptr,value) G_STMT_START {	\
	memcpy (ptr, value, strlen (value)+1);	\
	ptr += strlen (value)+1;		\
} G_STMT_END

typedef struct {
	gpointer stack_pointer;
	MonoObject *exception_obj;
	guint32 stop;
} MonoDebuggerExceptionInfo;

static int initialized = 0;

void
mono_debugger_lock (void)
{
	g_assert (initialized);
	EnterCriticalSection (&debugger_lock_mutex);
	debugger_lock_level++;
}

void
mono_debugger_unlock (void)
{
	g_assert (initialized);
	if (debugger_lock_level == 1) {
		if (must_reload_symtabs && mono_debugger_use_debugger) {
			mono_debugger_event (MONO_DEBUGGER_EVENT_RELOAD_SYMTABS, 0, 0);
			must_reload_symtabs = FALSE;
		}
	}

	debugger_lock_level--;
	LeaveCriticalSection (&debugger_lock_mutex);
}

void
mono_debugger_initialize (gboolean use_debugger)
{
	MONO_GC_REGISTER_ROOT (last_exception);
	
	g_assert (!mono_debugger_use_debugger);

	InitializeCriticalSection (&debugger_lock_mutex);
	mono_debugger_use_debugger = use_debugger;
	initialized = 1;
}

void
mono_debugger_add_symbol_file (MonoDebugHandle *handle)
{
	g_assert (mono_debugger_use_debugger);

	mono_debugger_lock ();
	mono_debugger_event (MONO_DEBUGGER_EVENT_ADD_MODULE, (guint64) (gsize) handle, 0);
	mono_debugger_unlock ();
}

void
mono_debugger_event (MonoDebuggerEvent event, guint64 data, guint64 arg)
{
	if (mono_debugger_event_handler)
		(* mono_debugger_event_handler) (event, data, arg);
}

void
mono_debugger_cleanup (void)
{
	mono_debugger_event (MONO_DEBUGGER_EVENT_FINALIZE_MANAGED_CODE, 0, 0);
	mono_debugger_event_handler = NULL;
}

gboolean
mono_debugger_unhandled_exception (gpointer addr, gpointer stack, MonoObject *exc)
{
	const gchar *name;

	if (!mono_debugger_use_debugger)
		return FALSE;

	// Prevent the object from being finalized.
	last_exception = exc;

	name = mono_class_get_name (mono_object_get_class (exc));
	if (!strcmp (name, "ThreadAbortException")) {
		MonoThread *thread = mono_thread_current ();
		mono_debugger_event (MONO_DEBUGGER_EVENT_THREAD_ABORT, 0, thread->tid);
		mono_thread_exit ();
	}

	mono_debugger_event (MONO_DEBUGGER_EVENT_UNHANDLED_EXCEPTION,
			     (guint64) (gsize) exc, (guint64) (gsize) addr);
	return TRUE;
}

void
mono_debugger_handle_exception (gpointer addr, gpointer stack, MonoObject *exc)
{
	MonoDebuggerExceptionInfo info;

	if (!mono_debugger_use_debugger)
		return;

	// Prevent the object from being finalized.
	last_exception = exc;

	info.stack_pointer = stack;
	info.exception_obj = exc;
	info.stop = 0;

	mono_debugger_event (MONO_DEBUGGER_EVENT_HANDLE_EXCEPTION, (guint64) (gsize) &info,
			     (guint64) (gsize) addr);
}

gboolean
mono_debugger_throw_exception (gpointer addr, gpointer stack, MonoObject *exc)
{
	MonoDebuggerExceptionInfo info;

	if (!mono_debugger_use_debugger)
		return FALSE;

	// Prevent the object from being finalized.
	last_exception = exc;

	info.stack_pointer = stack;
	info.exception_obj = exc;
	info.stop = 0;

	mono_debugger_event (MONO_DEBUGGER_EVENT_THROW_EXCEPTION, (guint64) (gsize) &info,
			     (guint64) (gsize) addr);
	return info.stop != 0;
}

static gchar *
get_exception_message (MonoObject *exc)
{
	char *message = NULL;
	MonoString *str; 
	MonoMethod *method;
	MonoClass *klass;
	gint i;

	if (mono_object_isinst (exc, mono_defaults.exception_class)) {
		klass = exc->vtable->klass;
		method = NULL;
		while (klass && method == NULL) {
			for (i = 0; i < klass->method.count; ++i) {
				method = klass->methods [i];
				if (!strcmp ("ToString", method->name) &&
				    mono_method_signature (method)->param_count == 0 &&
				    method->flags & METHOD_ATTRIBUTE_VIRTUAL &&
				    method->flags & METHOD_ATTRIBUTE_PUBLIC) {
					break;
				}
				method = NULL;
			}
			
			if (method == NULL)
				klass = klass->parent;
		}

		g_assert (method);

		str = (MonoString *) mono_runtime_invoke (method, exc, NULL, NULL);
		if (str)
			message = mono_string_to_utf8 (str);
	}

	return message;
}

MonoObject *
mono_debugger_runtime_invoke (MonoMethod *method, void *obj, void **params, MonoObject **exc)
{
	MonoObject *retval;
	gchar *message;

	if (!strcmp (method->name, ".ctor")) {
		retval = obj = mono_object_new (mono_domain_get (), method->klass);

		mono_runtime_invoke (method, obj, params, exc);
	} else
		retval = mono_runtime_invoke (method, obj, params, exc);

	if (!exc || (*exc == NULL))
		return retval;

	message = get_exception_message (*exc);
	if (message) {
		*exc = (MonoObject *) mono_string_new_wrapper (message);
		g_free (message);
	}

	return retval;
}

gboolean
mono_debugger_lookup_type (const gchar *type_name)
{
	int i;
	mono_debugger_lock ();

	for (i = 0; i < mono_symbol_table->num_symbol_files; i++) {
		MonoDebugHandle *symfile = mono_symbol_table->symbol_files [i];
		MonoType *type;
		MonoClass* klass;
		gchar *name;

		name = g_strdup (type_name);
		type = mono_reflection_type_from_name (name, symfile->image);
		g_free (name);
		if (!type)
			continue;

		klass = mono_class_from_mono_type (type);
		if (klass)
			mono_class_init (klass);

		mono_debugger_unlock ();
		return TRUE;
	}

	mono_debugger_unlock ();
	return FALSE;
}

gint32
mono_debugger_lookup_assembly (const gchar *name)
{
	MonoAssembly *assembly;
	MonoImageOpenStatus status;
	int i;

	mono_debugger_lock ();

 again:
	for (i = 0; i < mono_symbol_table->num_symbol_files; i++) {
		MonoDebugHandle *symfile = mono_symbol_table->symbol_files [i];

		if (!strcmp (symfile->image_file, name)) {
			mono_debugger_unlock ();
			return i;
		}
	}

	assembly = mono_assembly_open (name, &status);

	if (status != MONO_IMAGE_OK) {
		g_warning (G_STRLOC ": Cannot open image `%s'", name);
		mono_debugger_unlock ();
		return -1;
	}

	must_reload_symtabs = TRUE;
	goto again;
}

void
mono_debugger_add_type (MonoDebugHandle *symfile, MonoClass *klass)
{
	must_reload_symtabs = TRUE;

}