﻿using Sledge.BspEditor.Documents;
using Sledge.BspEditor.Environment;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Providers;
using Sledge.Common.Shell.Documents;
using Sledge.DataStructures.GameData;
using Sledge.Formats.Map.Formats;
using Sledge.Formats.Map.Objects;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SledgePrimitives = Sledge.BspEditor.Primitives;
using MapFormats = HammerTime.Formats;


namespace HammerTime.Formats.Providers
{
	[Export(typeof(IBspSourceProvider))]
	public class JmfBspSourceProvider : IBspSourceProvider
	{
		private static readonly IEnumerable<Type> SupportedTypes = new List<Type>
		{
            // Map Object types
            typeof(Solid),
			typeof(Entity),

            // Map Object Data types
            typeof(VisgroupID),
			typeof(EntityData),
		};
		public IEnumerable<Type> SupportedDataTypes => SupportedTypes;

		public IEnumerable<FileExtensionInfo> SupportedFileExtensions { get; } = new[]
		{
			new FileExtensionInfo("J.A.C.K. map formats", ".jmf", ".jmx"),
		};

		private static GameData _gameData;

		public async Task<BspFileLoadResult> Load(Stream stream, IEnvironment environment)
		{
			_gameData = await environment.GetGameData();
			return await Task.Factory.StartNew(() =>
			{

				var jmf = new Sledge.Formats.Map.Formats.JackhammerJmfFormat();
				var mapFile = jmf.Read(stream);
				var result = new BspFileLoadResult();
				var map = new SledgePrimitives.Map();


				map.Root.Data.Replace(MapFormats.Entity.FromFmt(mapFile.Worldspawn, map.NumberGenerator).EntityData);
				var objects = MapFormats.Prefab.GetPrefab(mapFile, map.NumberGenerator, map);


				foreach (var obj in objects)
				{
					obj.Hierarchy.Parent = map.Root;
				}

				result.Map = map;
				result.Map.Root.DescendantsChanged();
				return result;


			});
		}

		public Task Save(Stream stream, Map map, MapDocument document = null)
		{
			throw new NotImplementedException();
		}
	}
}
