using System;

namespace Mono.Debugger.Languages.Mono
{
	internal class MonoArrayObject : TargetArrayObject
	{
		public MonoArrayObject (MonoArrayType type, TargetLocation location)
			: base (type, location)
		{ }

		protected override void DoGetArrayBounds (TargetMemoryAccess target)
		{
			TargetBinaryReader reader = Location.ReadMemory (target, type.Size).GetReader ();

			reader.Position = 3 * reader.TargetMemoryInfo.TargetAddressSize;
			int length = reader.ReadInt32 ();

			if (Rank == 1) {
				bounds = TargetArrayBounds.MakeSimpleArray (length);
				return;
			}

			reader.Position = 2 * reader.TargetMemoryInfo.TargetAddressSize;
			TargetAddress bounds_address = new TargetAddress (
				target.AddressDomain, reader.ReadAddress ());
			TargetBinaryReader breader = target.ReadMemory (
				bounds_address, 8 * Rank).GetReader ();

			int[] lower = new int [Rank];
			int[] upper = new int [Rank];

			for (int i = 0; i < Rank; i++) {
				int b_length = breader.ReadInt32 ();
				int b_lower = breader.ReadInt32 ();

				lower [i] = b_lower;
				upper [i] = b_lower + b_length - 1;
			}

			bounds = TargetArrayBounds.MakeMultiArray (lower, upper);
		}

		internal override TargetObject GetElement (TargetMemoryAccess target, int[] indices)
		{
			int offset = GetArrayOffset (target, indices);

			TargetBlob blob;
			TargetLocation dynamic_location;
			try {
				blob = Location.ReadMemory (target, Type.Size);
				GetDynamicSize (target, blob, Location, out dynamic_location);
			} catch (TargetException ex) {
				throw new LocationInvalidException (ex);
			}

			TargetLocation new_loc = dynamic_location.GetLocationAtOffset (offset);

			if (Type.ElementType.IsByRef)
				new_loc = new_loc.GetDereferencedLocation ();

			if (new_loc.HasAddress && new_loc.GetAddress (target).IsNull)
				return new TargetNullObject (Type.ElementType);

			return Type.ElementType.GetObject (target, new_loc);
		}

		internal override void SetElement (TargetMemoryAccess target, int[] indices,
						   TargetObject obj)
		{
			int offset = GetArrayOffset (target, indices);

			TargetBlob blob;
			TargetLocation dynamic_location;
			try {
				blob = Location.ReadMemory (target, Type.Size);
				GetDynamicSize (target, blob, Location, out dynamic_location);
			} catch (TargetException ex) {
				throw new LocationInvalidException (ex);
			}

			TargetLocation new_loc = dynamic_location.GetLocationAtOffset (offset);

			Type.ElementType.SetObject (target, new_loc, obj);
		}

		internal override long GetDynamicSize (TargetMemoryAccess target, TargetBlob blob,
						       TargetLocation location,
						       out TargetLocation dynamic_location)
		{
			int element_size = Type.GetElementSize (target);
			dynamic_location = location.GetLocationAtOffset (Type.Size);
			return element_size * GetLength (target);
		}

		internal override string Print (TargetMemoryAccess target)
		{
			if (Location.HasAddress)
				return String.Format ("{0}", Location.GetAddress (target));
			else
				return String.Format ("{0}", Location);
		}

		public override bool HasClassObject {
			get { return true; }
		}

		internal override TargetClassObject GetClassObject (TargetMemoryAccess target)
		{
			return (TargetClassObject) Type.Language.ArrayType.GetObject (target, Location);
		}
	}
}
