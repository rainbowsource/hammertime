﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapData;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.Common;
using Sledge.Common.Shell.Documents;
using Sledge.Common.Transport;
using Sledge.DataStructures.Geometric;

namespace Sledge.BspEditor.Providers
{
    [Export(typeof(IBspSourceProvider))]
    public class VmfBspSourceProvider : IBspSourceProvider
    {
        [Import] private SerialisedObjectFormatter _formatter;
        [Import] private MapElementFactory _factory;

        private static readonly IEnumerable<Type> SupportedTypes = new List<Type>
        {
            // VMF supports everything because it's an extensible format
            typeof(IMapObject),
            typeof(IMapObjectData),
            typeof(IMapData),
        };

        public IEnumerable<Type> SupportedDataTypes => SupportedTypes;

        public IEnumerable<FileExtensionInfo> SupportedFileExtensions { get; } = new[]
        {
            new FileExtensionInfo("Valve map format", ".vmf", ".vmx"), 
        };

        public async Task<Map> Load(Stream stream)
        {
            return await Task.Factory.StartNew(() =>
            {
                var map = new Map();
                var so = _formatter.Deserialize(stream).ToList();

                foreach (var s in so) s.Name = s.Name?.ToLower();

                // If the sledge_native node is found, we should use it to maximise compatibility
                var native = so.FirstOrDefault(x => x.Name == "sledge_native");
                if (native != null)
                {
                    foreach (var o in native.Children)
                    {
                        if (o.Name == nameof(Root))
                        {
                            map.Root.Unclone((Root)_factory.Deserialise(o));
                        }
                        else
                        {
                            map.Data.Add((IMapData)_factory.Deserialise(o));
                        }
                    }
                }
                else
                {
                    // Load a VHE4 VMF format
                    LoadVisgroups(map, so.FirstOrDefault(x => x.Name == "visgroups"));
                    LoadWorld(map, so);
                    LoadCameras(map, so.FirstOrDefault(x => x.Name == "cameras"));
                    LoadCordon(map, so.FirstOrDefault(x => x.Name == "cordon"));
                    LoadViewSettings(map, so.FirstOrDefault(x => x.Name == "viewsettings"));
                }

                map.Root.DescendantsChanged();
                return map;
            });
        }

        private void LoadVisgroups(Map map, SerialisedObject visgroups)
        {
            if (visgroups == null) return;
            foreach (var vg in visgroups.Children.Where(x => x.Name?.ToLower() == "visgroup"))
            {
                var v = new Visgroup
                {
                    Name = vg.Get("name", ""),
                    ID = vg.Get("visgroupid", -1),
                    Colour = vg.GetColor("color"),
                    Visible = true
                };
                if (v.Colour.GetBrightness() < 0.001f) v.Colour = Colour.GetRandomBrushColour();
                if (v.ID < 0) continue;
                map.Data.Add(v);
            }
        }

        private void LoadWorld(Map map, List<SerialisedObject> objects)
        {
            var vos = objects.Select(VmfObject.Create).Where(vo => vo != null).ToList();

            var world = vos.OfType<VmfWorld>().FirstOrDefault();
            if (world == null) return;

            // A map of ids from the map -> ids from the vmf
            var mapToSource = new Dictionary<long, long>();
            
            world.Editor.Apply(map.Root);
            mapToSource.Add(map.Root.ID, world.ID);

            var tree = new List<VmfObject>();

            foreach (var vo in vos)
            {
                // Set the parent id to the world id
                if (vo.ID != world.ID && vo.Editor.ParentID == 0) vo.Editor.ParentID = world.ID;

                // Flatten the tree (nested hiddens -> no more hiddens)
                // (Flat tree includes self as well)
                var flat = vo.Flatten().ToList();

                // Set the default parent id for all the child objects
                foreach (var child in flat)
                {
                    if (child.Editor.ParentID == 0) child.Editor.ParentID = vo.ID;
                }

                // Add the objects to the tree
                tree.AddRange(flat);
            }

            tree.Remove(world);

            // All objects should have proper ids by now, get rid of anything with parentid 0 just in case
            var grouped = tree.GroupBy(x => x.Editor.ParentID).Where(x => x.Key > 0).ToDictionary(x => x.Key, x => x.ToList());
            
            // Step through each level of the tree and add them to their parent branches
            var leaves = new List<IMapObject> {map.Root};

            // Use a iteration limit of 1000. If the tree's that deep, I don't want to load your map anyway...
            for (var i = 0; i < 1000 && leaves.Any(); i++) // i.e. while (leaves.Any())
            {
                var newLeaves = new List<IMapObject>();
                foreach (var leaf in leaves)
                {
                    var sourceId = mapToSource[leaf.ID];
                    if (!grouped.ContainsKey(sourceId)) continue;

                    var items = grouped[sourceId];

                    // Create objects from items
                    foreach (var item in items)
                    {
                        var mapObject = item.ToMapObject(map.NumberGenerator);
                        mapToSource.Add(mapObject.ID, item.ID);
                        mapObject.Hierarchy.Parent = leaf;
                        newLeaves.Add(mapObject);
                    }
                }
                leaves = newLeaves;
            }

            // Now we should have a nice neat hierarchy of objects
        }

        private void LoadCameras(Map map, SerialisedObject cameras)
        {

        }

        private void LoadCordon(Map map, SerialisedObject cordon)
        {

        }

        private void LoadViewSettings(Map map, SerialisedObject viewsettings)
        {

        }

        public Task Save(Stream stream, Map map)
        {
            return Task.Factory.StartNew(() =>
            {
                var list = new List<SerialisedObject>
                {
                    _factory.Serialise(map.Root)
                };
                list.AddRange(map.Data.Select(_factory.Serialise));
                _formatter.Serialize(stream, list);
            });
        }

        private static bool ParseDecimalArray(string input, char[] splitChars, int expected, out decimal[] array)
        {
            var spl = input.Split(splitChars, StringSplitOptions.RemoveEmptyEntries);
            if (spl.Length == expected)
            {
                var parsed = spl.Select(x => decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal o) ? (decimal?)o : null).ToList();
                if (parsed.All(x => x.HasValue))
                {
                    // ReSharper disable once PossibleInvalidOperationException
                    array = parsed.Select(x => x.Value).ToArray();
                    return true;
                }
            }
            array = new decimal[expected];
            return false;
        }

        private abstract class VmfObject
        {
            public long ID { get; set; }
            public VmfEditor Editor { get; set; }

            protected VmfObject(SerialisedObject obj)
            {
                ID = obj.Get("id", 0L);
                Editor = new VmfEditor(obj.Children.FirstOrDefault(x => x.Name == "editor"));
            }

            public abstract IEnumerable<VmfObject> Flatten();
            public abstract IMapObject ToMapObject(UniqueNumberGenerator generator);

            public static VmfObject Create(SerialisedObject obj)
            {
                switch (obj.Name)
                {
                    case "world":
                        return new VmfWorld(obj);
                    case "entity":
                        return new VmfEntity(obj);
                    case "group":
                        return new VmfGroup(obj);
                    case "solid":
                        return new VmfSolid(obj);
                    case "hidden":
                        return new VmfHidden(obj);
                }
                return null;
            }
        }

        private class VmfEntity : VmfObject
        {
            public List<VmfObject> Objects { get; set; }
            public EntityData EntityData { get; set; }
            public Coordinate Origin { get; set; }

            private static readonly string[] ExcludedKeys = { "id", "spawnflags", "classname", "origin", "wad", "mapversion" };

            public VmfEntity(SerialisedObject obj) : base(obj)
            {
                Objects = new List<VmfObject>();
                foreach (var so in obj.Children)
                {
                    var o = VmfObject.Create(so);
                    if (o != null) Objects.Add(o);
                }

                var ed = new EntityData();
                foreach (var kv in obj.Properties)
                {
                    if (ExcludedKeys.Contains(kv.Key.ToLower())) continue;
                    ed.Set(kv.Key, kv.Value);
                }
                ed.Name = obj.Get("classname", "");
                ed.Flags = obj.Get("spawnflags", 0);
                EntityData = ed;

                if (obj.Properties.Any(x => x.Key == "origin"))
                {
                    Origin = obj.Get("origin", Coordinate.Zero);
                }
            }

            public override IEnumerable<VmfObject> Flatten()
            {
                return Objects.SelectMany(x => x.Flatten()).Union(new[] {this});
            }

            public override IMapObject ToMapObject(UniqueNumberGenerator generator)
            {
                var ent = new Entity(generator.Next("MapObject"));

                ent.Data.Add(EntityData);
                if (Origin != null) ent.Data.Add(new Origin(Origin));

                Editor.Apply(ent);

                return ent;
            }
        }

        private class VmfWorld : VmfEntity
        {
            public VmfWorld(SerialisedObject obj) : base(obj)
            {
            }

            public override IMapObject ToMapObject(UniqueNumberGenerator generator)
            {
                throw new NotSupportedException();
            }
        }

        private class VmfGroup : VmfObject
        {
            public VmfGroup(SerialisedObject obj) : base(obj)
            {
            }

            public override IEnumerable<VmfObject> Flatten()
            {
                yield return this;
            }

            public override IMapObject ToMapObject(UniqueNumberGenerator generator)
            {
                var grp = new Group(generator.Next("MapObject"));
                Editor.Apply(grp);
                return grp;
            }
        }

        private class VmfSolid : VmfObject
        {
            public List<VmfSide> Sides { get; set; }

            public VmfSolid(SerialisedObject obj) : base(obj)
            {
                Sides = new List<VmfSide>();
                foreach (var so in obj.Children.Where(x => x.Name == "side"))
                {
                    Sides.Add(new VmfSide(so));
                }
            }

            public override IEnumerable<VmfObject> Flatten()
            {
                yield return this;
            }

            public override IMapObject ToMapObject(UniqueNumberGenerator generator)
            {
                var sol = new Solid(generator.Next("MapObject"));
                Editor.Apply(sol);
                CreateFaces(sol, Sides, generator);
                return sol;
            }

            private void CreateFaces(Solid solid, List<VmfSide> sides, UniqueNumberGenerator generator)
            {
                // If all the sides don't have enough vertices, calculate them
                if (!sides.All(x => x.Vertices.Count >= 3))
                {
                    // We need to create the solid from intersecting planes
                    var poly = new Polyhedron(sides.Select(x => x.Plane));
                    foreach (var side in sides)
                    {
                        side.Vertices.Clear();
                        var pg = poly.Polygons.FirstOrDefault(x => x.GetPlane().Normal.EquivalentTo(side.Plane.Normal));
                        if (pg == null)
                        {
                            continue;
                        }
                        side.Vertices.AddRange(pg.Vertices);
                    }
                }

                // We know the vertices, now create the faces
                foreach (var side in sides)
                {
                    var face = new Face(generator.Next("Face"))
                    {
                        Plane = side.Plane,
                        Texture = side.Texture
                    };
                    face.Vertices.AddRange(side.Vertices);
                    solid.Data.Add(face);
                }

                solid.DescendantsChanged();
            }
        }

        private class VmfHidden : VmfObject
        {
            public List<VmfObject> Objects { get; set; }

            public VmfHidden(SerialisedObject obj) : base(obj)
            {
                Objects = new List<VmfObject>();
                foreach (var so in obj.Children)
                {
                    var o = VmfObject.Create(so);
                    if (o != null) Objects.Add(o);
                }
            }

            public override IEnumerable<VmfObject> Flatten()
            {
                return Objects.SelectMany(x => x.Flatten());
            }

            public override IMapObject ToMapObject(UniqueNumberGenerator generator)
            {
                throw new NotSupportedException();
            }
        }

        private class VmfSide
        {
            public long ID { get; set; }
            public Plane Plane { get; set; }
            public Texture Texture { get; set; }
            public decimal LightmapScale { get; set; }
            public string SmoothingGroups { get; set; } // ?
            public List<Coordinate> Vertices { get; set; }

            public VmfSide(SerialisedObject obj)
            {
                ID = obj.Get("ID", 0L);
                LightmapScale = obj.Get("lightmapscale", 0);
                SmoothingGroups = obj.Get("smoothing_groups", "");

                if (ParseDecimalArray(obj.Get("plane", ""), new[] {' ', '(', ')'}, 9, out decimal[] pl))
                {
                    Plane = new Plane(
                        new Coordinate(pl[0], pl[1], pl[2]).Round(),
                        new Coordinate(pl[3], pl[4], pl[5]).Round(),
                        new Coordinate(pl[6], pl[7], pl[8]).Round());
                }
                else
                {
                    Plane = new Plane(Coordinate.UnitZ, 0);
                }

                Texture = new Texture
                {
                    Name = obj.Get("material", ""),
                    Rotation = obj.Get("rotation", 0m)
                };
                if (ParseDecimalArray(obj.Get("uaxis", ""), new[] {' ', '[', ']'}, 5, out decimal[] ua))
                {
                    Texture.UAxis = new Coordinate(ua[0], ua[1], ua[2]);
                    Texture.XShift = ua[3];
                    Texture.XScale = ua[4];
                }
                if (ParseDecimalArray(obj.Get("vaxis", ""), new[] {' ', '[', ']'}, 5, out decimal[] va))
                {
                    Texture.VAxis = new Coordinate(va[0], va[1], va[2]);
                    Texture.YShift = va[3];
                    Texture.YScale = va[4];
                }

                // Older versions of sledge save vertices, this is entirely optional but why not.
                Vertices = new List<Coordinate>();
                var verts = obj.Children.FirstOrDefault(x => x.Name == "vertex");
                if (verts == null) return;

                var count = obj.Get("count", 0);
                for (var i = 0; i < count; i++)
                {
                    var pt = obj.Get<Coordinate>("vertex" + i);
                    if (pt == null)
                    {
                        Vertices.Clear();
                        break;
                    }
                    Vertices.Add(pt);
                }
            }
        }

        private class VmfEditor
        {
            public Color Color { get; set; }
            public bool VisgroupShown { get; set; }
            public bool VisgroupAutoShown { get; set; }
            public List<long> VisgroupIDs { get; set; }
            public long ParentID { get; set; }

            public VmfEditor(SerialisedObject obj)
            {
                if (obj == null) obj = new SerialisedObject("editor");

                Color = obj.GetColor("color");
                var groupId = obj.Get("groupid", 0);
                ParentID = groupId > 0 ? groupId : obj.Get("parentid", 0);
                VisgroupShown = obj.Get("visgroupshown", "1") == "1";
                VisgroupAutoShown = obj.Get("visgroupautoshown", "1") == "1";
                // logicalpos?

                VisgroupIDs = new List<long>();
                foreach (var vid in obj.Properties.Where(x => x.Key == "visgroupid"))
                {
                    if (long.TryParse(vid.Value, out long id)) VisgroupIDs.Add(id);
                }
            }

            public void Apply(IMapObject obj)
            {
                var c = Color.GetBrightness() > 0 ? Color : Colour.GetRandomBrushColour();
                obj.Data.Replace(new ObjectColor(c));
                foreach (var id in VisgroupIDs)
                {
                    obj.Data.Add(new VisgroupID(id));
                }
            }
        }
    }
}