/*
 * console-io.h: Console IO internal calls
 *
 * Author:
 *	Gonzalo Paniagua Javier (gonzalo@ximian.com)
 *
 * Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
 */

#ifndef _MONO_METADATA_CONSOLEIO_H
#define _MONO_METADATA_CONSOLEIO_H

#include <config.h>
#include <glib.h>

#include <mono/metadata/object.h>
#include <mono/io-layer/io-layer.h>

G_BEGIN_DECLS

MonoBoolean ves_icall_System_ConsoleDriver_Isatty (HANDLE handle) MONO_INTERNAL;
gint32 ves_icall_System_ConsoleDriver_InternalKeyAvailable (gint32 timeout) MONO_INTERNAL;
MonoBoolean ves_icall_System_ConsoleDriver_SetEcho (MonoBoolean echo) MONO_INTERNAL;
MonoBoolean ves_icall_System_ConsoleDriver_SetBreak (MonoBoolean want_break) MONO_INTERNAL;
MonoBoolean ves_icall_System_ConsoleDriver_TtySetup (MonoString *teardown, char *verase, char *vsusp, char *intr) MONO_INTERNAL;
MonoBoolean ves_icall_System_ConsoleDriver_GetTtySize (HANDLE handle, gint32 *width, gint32 *height) MONO_INTERNAL;
void ves_icall_System_ConsoleDriver_Suspend (void) MONO_INTERNAL;

G_END_DECLS

#endif /* _MONO_METADATA_CONSOLEIO_H */
