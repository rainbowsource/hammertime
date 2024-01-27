﻿using SledgeRegular = Sledge.BspEditor.Primitives.MapObjects;
using SledgeFormats = Sledge.Formats.Map.Objects;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapObjectData;


namespace HammerTime.Formats
{
	internal class Group
	{
		public static SledgeRegular.Group FromFmt(SledgeFormats.Group Group, UniqueNumberGenerator uniqueNumberGenerator)
		{
			var group = new SledgeRegular.Group(uniqueNumberGenerator.Next("MapObject"));
			group.Data.Add(new ObjectColor(Group.Color));

			foreach (var children in Group.Children)
			{
				MapObject.GetMapObject(children, uniqueNumberGenerator).Hierarchy.Parent = group;
			}
			
			group.DescendantsChanged();
			return group;
		}
	}
}
