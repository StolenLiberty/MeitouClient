# Ogre mesh

Format research from Ogre serializer/source and base-game reader scans; versions/chunks/vertex
fields retained below. `OgreMeshReader` reads geometry/weights/poses/LOD; [skeleton](ogre-skeleton.md)
owns bone/animation layout. [Characters](../characters.md), [materials](ogre-material.md).

## Stream and compatibility

Little-endian header ushort0x1000 then newline-terminated version string, NO header length.
Primitive bool1/ushort2/uint-float4; strings newline with trailing CR dropped. Ordinary chunks
ushortID+uintLength INCLUDING6-byte header. Children are parsed structurally, not blindly skipped
by parent length: exporter bugs exist. Byte-swapped endian header is detected but unsupported.
Keep shared versus submesh-local vertices, index width, declaration source/offset/type/semantic
and bone assignments distinct. Bounds/mesh/skeleton links and manual/generated LOD are separate.

Pose chunks map affected vertices, optional normal deltas and target submesh; LOD can store linked
manual meshes or reduced indices. Some formats/versions alter these layouts; use the retained
version/chunk tables, not one guessed body size. Character morph, hidden-part and mesh-specific
LOD rules are data/research, not arbitrary importer conventions. Unknown chunk/unsupported
construct must fail clearly rather than silently reinterpret vertex bytes.

**Verified** base-file reader checks cover named versions/chunks/poses; that does not establish
support for every exporter or mod. Preserve contradictory stored lengths and documented parse
exceptions where structure is known. Current render conversion does not define the source format.

### Versions in the base game (Verified)

| Header string | Files | Ogre serializer |
| --- | ---: | --- |
| `[MeshSerializer_v1.100]` | 1,740 | `MeshSerializerImpl` (current in Ogre 2.0) |
| `[MeshSerializer_v1.8]` | 1,460 | `MeshSerializerImpl_v1_8` |
| `[MeshSerializer_v1.41]` | 77 | `MeshSerializerImpl_v1_41` (same as 1.8 except pose/morph data) |

Mods: `[MeshSerializer_v1.40]` (`MeshSerializerImpl_v1_4`; **Observed** in 99 Workshop meshes of four mods, e.g. Merc Armor Bundle 2875287426) is 1.41 except
the LOD block: no strategy name (Ogre 1.9 gives the mesh `distance_sphere`) and each level's value is the distance squared (`userValue = sqrt(value)`), per Ogre 1.9.1
`MeshSerializerImpl_v1_4::readMeshLodInfo`. Kenshi's Ogre 1.9 reads it; a reader that refuses it leaves the item invisible and without an icon. **Verified** that
`OgreMeshReader` reads four of those files; none of them has a LOD block, so the LOD difference is not checked against a file.

### Chunks — layout/example

```
0x1000 HEADER            ushort id + version string (no length)
0x3000 MESH              bool skeletallyAnimated
  0x5000 GEOMETRY        shared vertices (none in Kenshi: Verified 0 meshes use shared vertices)
  0x4000 SUBMESH         string material, bool useSharedVertices, uint indexCount, bool indexes32Bit,
                         ushort/uint indices[indexCount]
    0x5000 GEOMETRY      required if !useSharedVertices
    0x4010 SUBMESH_OPERATION      ushort operation (1 points, 2 lines, 3 line strip, 4 triangle list (default), 5 strip, 6 fan)
    0x4100 SUBMESH_BONE_ASSIGNMENT uint vertex, ushort bone, float weight   (repeats)
    0x4200 SUBMESH_TEXTURE_ALIAS   string alias, string texture              (repeats)
  0x6000 MESH_SKELETON_LINK       string skeleton file name
  0x7000 MESH_BONE_ASSIGNMENT     as 0x4100, for shared vertices
  0x8000 MESH_LOD_LEVEL           see LOD below
  0x9000 MESH_BOUNDS              float min x,y,z, max x,y,z, radius
  0xA000 SUBMESH_NAME_TABLE
    0xA100 ..._ELEMENT            ushort submesh index, string name          (repeats)
  0xB000 EDGE_LISTS               stencil-shadow edges (walked, not kept)
  0xC000 POSES                     see Pose chunks below
  0xD000 ANIMATIONS, 0xE000 TABLE_EXTREMES   skipped by length

GEOMETRY (0x5000): uint vertexCount, then
  0x5100 VERTEX_DECLARATION
    0x5110 VERTEX_ELEMENT         ushort source, type, semantic, offset, index   (repeats)
  0x5200 VERTEX_BUFFER            ushort bindIndex, ushort vertexSize            (repeats)
    0x5210 VERTEX_BUFFER_DATA     vertexCount * vertexSize bytes (interleaved elements of that source)
```

### Pose chunks (Verified) — layout/example

```
0xC000 POSES
  0xC100 POSE          string name, ushort target (0 = shared vertices, else submesh index + 1),
                       [1.100 / 1.8] bool includesNormals                     (repeats)
    0xC111 POSE_VERTEX uint vertex index, Vector3 offset, [Vector3 normal if includesNormals]   (repeats)
```

### Poses: character morph targets

| Mesh (`character/meshes/...`) | Poses | Names (examples) |
| --- | ---: | --- |
| `human/human_male.mesh`, `human_female.mesh` | 23, 29 | `wide_cheekbones`, `long_nose`, `big_mouth`, `tiltup_eyes`, `overbite`, `wide_jaw` |
| `bone/bone_male.mesh`, `bone_female.mesh` | 28, 35 | `bone_` + face names, plus `bone_horns_curved`, `bone_horns_top_short`, `bone_horns_bottom_short`, `bone_horns_thick`, ... |
| `stick_person/Stick_person*.mesh` (3) | 9 each | `stick_big_eyes`, `stick_long_antenna`, ... |
| `human/` limb-stump meshes (6) | 23 or 29 | `0` ... `22` / `0` ... `28` |

## Open questions

- How pose weights are derived from appearance values. Observed: body files store values under the pose
  names themselves ([../characters.md](../characters.md#shading-poses-and-lod)); whether Kenshi scales them is Unknown.

## Expression reference

- Pose chunks: ` 0xC000 POSES 0xC100 POSE string name, ushort target (0 = shared vertices, else submesh index + 1), [1.100 / 1.8] bool includesNormals (repeats) 0xC111 POSE_VERTEX uint vertex index, Vector3 offset, [Vector3 normal if includesNormals] (repeats) `
- LOD: `lod1=8000,p,1.0`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Versions in the base game (Verified)
- Pose chunks (Verified)
- LOD (0x8000)
- Other base-game facts (Verified)
