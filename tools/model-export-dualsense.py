"""把 CasperH2O 的 Sony DualSense .blend 转成 PadShift 场景用的 OBJ+MTL。

在本仓库 tools/model-work 的 bpy 虚拟环境里运行:
    .venv/Scripts/python.exe ../model-export-dualsense.py

与 xbox 版导出的差别只有 group_for 的对象名映射（DualSense 的 face 键叫 B1..B4，
十字键带 Cover/Arrow 变体，Special 是 PS 键）。坐标变换与减面策略完全一致。
"""
import os

import bpy

repo = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
out_dir = os.path.join(repo, "assets", "3d", "dualsense-cc0")
blend = os.path.join(out_dir, "source.blend")
obj_path = os.path.join(out_dir, "dualsense.obj")
mtl_path = os.path.join(out_dir, "dualsense.mtl")

bpy.ops.wm.open_mainfile(filepath=blend)
deps = bpy.context.evaluated_depsgraph_get()

# Keep the hero model under the 40k budget: collapse-decimate every mesh, but leave tiny
# symbols (a few dozen triangles) untouched so the legends stay legible.
TARGET_TOTAL = 33000
total_before = 0
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
for o in meshes:
    me = o.evaluated_get(deps).to_mesh()
    total_before += sum(len(p.vertices) - 2 for p in me.polygons)
    o.evaluated_get(deps).to_mesh_clear()

ratio = min(1.0, TARGET_TOTAL / max(total_before, 1))
for o in meshes:
    tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
    r = 1.0 if tris < 250 else ratio
    if r >= 1.0:
        continue
    mod = o.modifiers.new("PadShiftDecimate", 'DECIMATE')
    mod.decimate_type = 'COLLAPSE'
    mod.ratio = r
    mod.use_collapse_triangulate = True
    bpy.context.view_layer.objects.active = o
    o.select_set(True)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    o.select_set(False)
    print("DECIMATE", o.name, tris, "->", sum(len(p.vertices) - 2 for p in o.data.polygons))


def group_for(name):
    # Face cluster (PS symbols mapped to the Xbox-convention semantic names, like the reference tool):
    # B1 = cross (bottom) -> A, B2 = circle (right) -> B, B3 = square (left) -> X,
    # B4 = triangle (top) -> Y. The Button/Symbol suffixes belong to the same physical key.
    if name in ("B1", "B1Button", "B1ButtonSymbol"):
        return "A"
    if name in ("B2", "B2Button", "B2ButtonSymbol"):
        return "B"
    if name in ("B3", "B3Button", "B3ButtonSymbol"):
        return "X"
    if name in ("B4", "B4Button", "B4ButtonSymbol"):
        return "Y"
    if name == "LeftThumb":
        return "StickLeft"
    if name == "RightThumb":
        return "StickRight"
    # The joystick rings are the rim of the stick cap assembly (same height as the dished top,
    # slightly wider than the cap wall), so they must tilt together with the stick.
    if name == "Joystick-Left-Ring":
        return "StickLeft"
    if name == "Joystick-Right-Ring":
        return "StickRight"
    # D-pad arms: the key cap (name), its cover and the moulded arrow move as one piece.
    if name in ("DPadUp", "DPadUpCover", "DPadUpArrow"):
        return "DpadUp"
    if name in ("DPadDown", "DPadDownCover", "DPadDownArrow"):
        return "DpadDown"
    if name in ("DPadLeft", "DPadLeftCover", "DPadLeftArrow"):
        return "DpadLeft"
    if name in ("DPadRight", "DPadRightCover", "DPadRightArrow"):
        return "DpadRight"
    if name == "Shoulder-Left-Trigger":
        return "LT"
    if name == "Shoulder-Right-Trigger":
        return "RT"
    if name == "LeftShoulder":
        return "LB"
    if name == "RightShoulder":
        return "RB"
    if name == "Start":
        return "Menu"
    if name == "Back":
        return "View"
    if name == "Special":
        return "Guide"
    return "Body"


COLORS = {
    "Body": (0.150, 0.160, 0.180),
    "StickLeft": (0.060, 0.066, 0.078),
    "StickRight": (0.060, 0.066, 0.078),
    "A": (0.180, 0.620, 0.220),
    "B": (0.780, 0.180, 0.150),
    "X": (0.130, 0.380, 0.850),
    "Y": (0.910, 0.700, 0.110),
    "Guide": (0.700, 0.740, 0.800),
    "LB": (0.150, 0.165, 0.195),
    "RB": (0.150, 0.165, 0.195),
    "LT": (0.130, 0.145, 0.175),
    "RT": (0.130, 0.145, 0.175),
    "DpadUp": (0.260, 0.285, 0.340),
    "DpadDown": (0.260, 0.285, 0.340),
    "DpadLeft": (0.260, 0.285, 0.340),
    "DpadRight": (0.260, 0.285, 0.340),
    "View": (0.220, 0.240, 0.280),
    "Menu": (0.220, 0.240, 0.280),
}


def tx(p):
    return (p.x, p.z, -p.y)   # Blender Z-up -> PadShift Y-up, face (Blender -Y) -> +Z


def tn(n):
    return (n.x, n.z, -n.y)


order = []
objects_by_group = {}
for o in meshes:
    g = group_for(o.name)
    if g not in objects_by_group:
        objects_by_group[g] = []
        order.append(g)
    objects_by_group[g].append(o)

obj_lines = [
    "# PadShift Sony DualSense controller - converted from source.blend (CC0)",
    "mtllib dualsense.mtl",
]
mtl_lines = ["# PadShift DualSense materials"]
obj_index = 0
normal_index = 0
normal_map = {}

for g in order:
    r, gg, b = COLORS[g]
    mtl_lines.append(f"newmtl {g}")
    mtl_lines.append(f"Kd {r:.4f} {gg:.4f} {b:.4f}")
    mtl_lines.append("Ks 0.4 0.4 0.4")
    mtl_lines.append("Ns 52")
    mtl_lines.append("")
    obj_lines.append(f"o {g}")
    obj_lines.append(f"usemtl {g}")

    for o in objects_by_group[g]:
        eo = o.evaluated_get(deps)
        me = eo.to_mesh()
        me.calc_loop_triangles()
        mw = eo.matrix_world
        nm = mw.to_3x3().inverted().transposed()
        base_v = obj_index
        for v in me.vertices:
            x, y, z = tx(mw @ v.co)
            obj_lines.append(f"v {x:.6f} {y:.6f} {z:.6f}")
        obj_index += len(me.vertices)

        loop_normals = []
        for corner in me.corner_normals:
            n = nm @ corner.vector
            n.normalize()
            key = tuple(round(c, 4) for c in tn(n))
            idx = normal_map.get(key)
            if idx is None:
                obj_lines.append(f"vn {key[0]:.4f} {key[1]:.4f} {key[2]:.4f}")
                normal_index += 1
                idx = normal_index
                normal_map[key] = idx
            loop_normals.append(idx)

        for tri in me.loop_triangles:
            parts = []
            for li in tri.loops:
                vi = base_v + me.loops[li].vertex_index + 1
                parts.append(f"{vi}//{loop_normals[li]}")
            obj_lines.append("f " + " ".join(parts))
        eo.to_mesh_clear()

with open(mtl_path, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(mtl_lines))
with open(obj_path, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(obj_lines))
    f.write("\n")

total_after = sum(1 for line in obj_lines if line.startswith("f "))
print("BEFORE", total_before, "AFTER", total_after, "OBJ", os.path.getsize(obj_path))
