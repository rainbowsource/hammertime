using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Numerics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Sledge.BspEditor.Documents;
using Sledge.BspEditor.Primitives.MapData;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.BspEditor.Rendering.Resources;
using Sledge.DataStructures.Geometric;
using Sledge.Providers.Texture;
using Sledge.Rendering.Cameras;
using Sledge.Rendering.Pipelines;
using Sledge.Rendering.Primitives;
using Sledge.Rendering.Resources;
using System;

namespace Sledge.BspEditor.Rendering.Converters
{
	[Export(typeof(IMapObjectSceneConverter))]
	public class DefaultSolidConverter : IMapObjectSceneConverter
	{
		public MapObjectSceneConverterPriority Priority => MapObjectSceneConverterPriority.DefaultLowest;

		public bool ShouldStopProcessing(MapDocument document, IMapObject obj)
		{
			return false;
		}

		public bool Supports(IMapObject obj)
		{
			return obj.Data.OfType<Face>().Any();
		}

		public Task Convert(BufferBuilder builder, MapDocument document, IMapObject obj, ResourceCollector resourceCollector)
		{
			return ConvertFaces(builder, document, obj, obj.Data.Get<Face>().ToList(), resourceCollector);
		}
		internal static async Task ConvertFaces(BufferBuilder builder, MapDocument document, IMapObject obj, List<Face> faces, ResourceCollector resourceCollector)
		{
			faces = faces.Where(x => x.Vertices.Count > 2).ToList();

			var displayFlags = document.Map.Data.GetOne<DisplayFlags>();
			var displayData = document.Map.Data.GetOne<DisplayData>() ?? new DisplayData();
			var hideNull = displayFlags?.HideNullTextures == true;
			var hideClip = displayFlags?.HideClipTextures == true;
			var wireframe = displayFlags?.Wireframe == true;
			var skybox = displayFlags?.ToggleSkybox == true;

			// Pack the vertices like this [ f1v1 ... f1vn ] ... [ fnv1 ... fnvn ]
			var numVertices = (uint)faces.Sum(x => x.Vertices.Count);

			// Pack the indices like this [ solid1 ... solidn ] [ wireframe1 ... wireframe n ]
			var numSolidIndices = (uint)faces.Sum(x => (x.Vertices.Count - 2) * 3);
			var numWireframeIndices = numVertices * 2;

			var points = new VertexStandard[numVertices];
			var indices = new uint[numSolidIndices + numWireframeIndices];
			Color? vColor = null;
			var vId = obj.Data.GetOne<VisgroupID>();
			if (vId != null)
			{
				vColor = document.Map.Data.Get<Visgroup>().FirstOrDefault(x=>x.ID == vId.ID)?.Colour;
			}

			var colour = (obj.IsSelected ? Color.Red : vColor.HasValue? vColor.Value : obj.Data.GetOne<ObjectColor>()?.Color ?? Color.White).ToVector4();

			var tint = Vector4.One;

			var tc = await document.Environment.GetTextureCollection();

			var pipeline = PipelineType.TexturedOpaque;
			var entityHasTransparency = false;
			var flags = obj.IsSelected ? VertexFlags.SelectiveTransformed : VertexFlags.None;

			// try and find the parent entity for render flags
			// TODO: this code is extremely specific to Goldsource and should be abstracted away
			var parentEntity = obj.FindClosestParent(x => x is Entity) as Entity;
			if (parentEntity?.EntityData != null)
			{
				const int renderModeColor = 1;
				const int renderModeTexture = 2;
				const int renderModeGlow = 3; // same as texture for brushes
				const int renderModeSolid = 4;
				const int renderModeAdditive = 5;

				var rendermodevalue = parentEntity.EntityData.GetStringProperty("rendermode", "0");
				var renderamtvalue = parentEntity.EntityData.GetStringProperty("renderamt", "255");
				int rendermode;
				float renderamt;
				if (!String.IsNullOrEmpty(rendermodevalue))
				{
					rendermode = int.Parse(rendermodevalue);
				}
				else
				{
					rendermode = 0;
				}
				if (!String.IsNullOrEmpty(renderamtvalue))
				{
					renderamt = float.Parse(renderamtvalue) / 255f;
				}
				else
				{
					renderamt = 1;
				}

				//var rendermode = renderModeSolid;
				//var renderamt = 1;

				entityHasTransparency = renderamt < 0.99;

				switch (rendermode)
				{
					case renderModeColor:
						// Flat colour, use render colour and force it to run through the alpha tested pipeline
						var rendercolor = parentEntity.EntityData.GetVector3("rendercolor") / 255f ?? Vector3.One;
						tint = new Vector4(rendercolor, renderamt);
						flags |= VertexFlags.FlatColour | VertexFlags.AlphaTested;
						pipeline = PipelineType.TexturedAlpha;
						entityHasTransparency = true;
						break;
					case renderModeTexture:
					case renderModeGlow:
						// Texture is alpha tested and can be transparent
						tint = new Vector4(1, 1, 1, renderamt);
						flags |= VertexFlags.AlphaTested;
						if (entityHasTransparency) pipeline = PipelineType.TexturedAlpha;
						break;
					case renderModeSolid:
						// Texture is alpha tested only
						flags |= VertexFlags.AlphaTested;
						entityHasTransparency = false;
						break;
					case renderModeAdditive:
						// Texture is alpha tested and transparent, force through the additive pipeline
						tint = new Vector4(renderamt, renderamt, renderamt, 1);
						pipeline = PipelineType.TexturedAdditive;
						entityHasTransparency = true;
						break;
					default:
						entityHasTransparency = false;
						break;
				}
			}

			if (obj.IsSelected) tint *= new Vector4(1, 0.5f, 0.5f, 1);

			var vi = 0u;
			var si = 0u;
			var wi = numSolidIndices;
			foreach (var face in faces)
			{
				var opacity = tc.GetOpacity(face.Texture.Name);
				var t = await tc.GetTextureItem(face.Texture.Name);
				var w = t?.Width ?? 0;
				var h = t?.Height ?? 0;

				var tintModifier = new Vector4(1, 1, 1, opacity);
				var extraFlags = t == null ? VertexFlags.FlatColour : VertexFlags.None;

				var offs = vi;
				var numFaceVerts = (uint)face.Vertices.Count;

				var textureCoords = face.GetTextureCoordinates(w, h).ToList();

				var normal = face.Plane.Normal;
				for (var i = 0; i < face.Vertices.Count; i++)
				{
					var v = face.Vertices[i];
					points[vi++] = new VertexStandard
					{
						Position = v,
						Colour = colour,
						Normal = normal,
						Texture = new Vector2(textureCoords[i].Item2, textureCoords[i].Item3),
						Tint = tint * tintModifier,
						Flags = flags | extraFlags
					};
				}

				// Triangles - [0 1 2]  ... [0 n-1 n]
				for (uint i = 2; i < numFaceVerts; i++)
				{
					indices[si++] = offs;
					indices[si++] = offs + i - 1;
					indices[si++] = offs + i;
				}

				// Lines - [0 1] ... [n-1 n] [n 0]
				for (uint i = 0; i < numFaceVerts; i++)
				{
					indices[wi++] = offs + i;
					indices[wi++] = offs + (i == numFaceVerts - 1 ? 0 : i + 1);
				}
			}

			var groups = new List<BufferGroup>();

			uint texOffset = 0;
			foreach (var f in faces)
			{
				var texInd = (uint)(f.Vertices.Count - 2) * 3;

				if ((hideNull && tc.IsNullTexture(f.Texture.Name)) || (hideClip && tc.IsClipTexture(f.Texture.Name) || (skybox && f.Texture.Name.ToLower() == "sky")))
				{
					texOffset += texInd;
					continue;
				}

				var opacity = tc.GetOpacity(f.Texture.Name);
				var t = await tc.GetTextureItem(f.Texture.Name);
				var transparent = entityHasTransparency || opacity < 0.95f || t?.Flags.HasFlag(TextureFlags.Transparent) == true;

				var texture = t == null ? string.Empty : $"{document.Environment.ID}::{f.Texture.Name}";
				BufferGroup group;

				//				if(skybox && f.Texture.Name.ToLower() == "sky")
				//				{
				//					group = new BufferGroup(
				//    PipelineType.TexturedAlpha,
				//   CameraType.Perspective, true, f.Origin, texture, texOffset, texInd
				//);
				//					flags |= VertexFlags.FlatColour;
				//					points = points.Select(x => { x.Tint = new Vector4(1,1,1, 0.5f); return x; }).ToArray();
				//				}
				//else
				{
					group = new BufferGroup(
					   pipeline == PipelineType.TexturedOpaque && transparent ? PipelineType.TexturedAlpha : pipeline,
					   CameraType.Perspective, transparent, f.Origin, texture, texOffset, texInd
				   );
				}
				groups.Add(group);


				texOffset += texInd;

				if (t != null) resourceCollector.RequireTexture(t.Name);
			}

			groups.Add(new BufferGroup(PipelineType.Wireframe, obj.IsSelected ? CameraType.Both : CameraType.Orthographic, numSolidIndices, numWireframeIndices));

			builder.Append(points, indices, groups);
			if (wireframe)
			{
				var wirePoints = points.ToList().Select(x => { x.Flags |= VertexFlags.Wireframed; return x; });
				builder.Append(wirePoints, indices, new[] { new BufferGroup(PipelineType.Wireframe, CameraType.Perspective, numSolidIndices, numWireframeIndices) });
			}
			// Also push the untransformed wireframe when selected
			if (obj.IsSelected)
			{
				for (var i = 0; i < points.Length; i++) points[i].Flags = VertexFlags.None;
				var untransformedIndices = indices.Skip((int)numSolidIndices);
				builder.Append(points, untransformedIndices, new[]
				{
					new BufferGroup(PipelineType.Wireframe, CameraType.Both, 0, numWireframeIndices)
				});
			}
		}
	}
}