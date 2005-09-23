using System;

namespace Mono.Debugger.Languages.Native
{
	internal abstract class NativeObject : MarshalByRefObject, ITargetObject
	{
		protected ITargetType type;
		protected TargetLocation location;

		public NativeObject (ITargetType type, TargetLocation location)
		{
			this.type = type;
			this.location = location;
		}

		public ITargetType Type {
			get {
				return type;
			}
		}

		public string TypeName {
			get {
				return type.Name;
			}
		}

		public TargetObjectKind Kind {
			get {
				return type.Kind;
			}
		}

		public bool IsNull {
			get {
				if (!location.HasAddress)
					return false;
				else
					return location.Address.IsNull;
			}
		}

		public virtual byte[] RawContents {
			get {
				try {
					return location.ReadBuffer (type.Size);
				} catch (TargetException ex) {
					throw new LocationInvalidException (ex);
				}
			}
			set {
				try {
					if (!type.HasFixedSize || (value.Length != type.Size))
						throw new ArgumentException ();
					location.WriteBuffer (value);
				} catch (TargetException ex) {
					throw new LocationInvalidException (ex);
				}
			}
		}

		protected virtual int MaximumDynamicSize {
			get {
				return -1;
			}
		}

		public virtual long DynamicSize {
			get {
				if (type.HasFixedSize)
					throw new InvalidOperationException ();

				try {
					TargetLocation dynamic_location;
					TargetBlob blob = location.ReadMemory (type.Size);
					return GetDynamicSize (blob, location, out dynamic_location);
				} catch (TargetException ex) {
					throw new LocationInvalidException (ex);
				}
			}
		}

		public virtual byte[] GetRawDynamicContents (int max_size)
		{
			if (type.HasFixedSize)
				throw new InvalidOperationException ();

			try {
				return GetDynamicContents (location, max_size).Contents;
			} catch (TargetException ex) {
				throw new LocationInvalidException (ex);
			}
		}

		protected virtual TargetBlob GetDynamicContents (TargetLocation location,
								 int max_size)
		{
			try {
				TargetLocation dynamic_location;
				TargetBlob blob = location.ReadMemory (type.Size);
				long size = GetDynamicSize (blob, location, out dynamic_location);

				if ((max_size > 0) && (size > (long) max_size))
					size = max_size;

				return dynamic_location.ReadMemory ((int) size);
			} catch (TargetException ex) {
				throw new LocationInvalidException (ex);
			}
		}

		protected abstract long GetDynamicSize (TargetBlob blob, TargetLocation location,
							out TargetLocation dynamic_location);

		public TargetLocation Location {
			get {
				return location;
			}
		}

		public virtual string Print (ITargetAccess target)
		{
			return ToString ();
		}

		public override string ToString ()
		{
			return String.Format ("{0} [{1}]", GetType (), Type);
		}
	}
}