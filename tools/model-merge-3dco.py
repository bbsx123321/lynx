"""把 3dco+（3D Controller Overlay）的分部件手柄 OBJ 合并成 PadShift 场景单 OBJ+MTL。

用法:
    python model-merge-3dco.py <部件目录> <输出目录> <输出名>

读取目录里的 info.json 取每个部件的装配偏移；摇杆的 stick/cap 复用同侧 ring 的
偏移（三者是一个组件）。坐标从"平放姿态"（X 右 / Y 上法向 / -Z 上端）转到 PadShift
的展示姿态（X 右 / Y 上 / Z 朝相机）：tx(p) = (p.x, -p.z, p.y)。
"""
import json
import os
import sys

# 文件名 -> (组名, 是否用 ring 偏移)。未列出的文件忽略。
PART_TO_GROUP = {
    "top_shell.obj": ("Body", False),
    "bottom_shell.obj": ("Body", False),
    "extra.obj": ("Body", False),
    "misc.obj": ("Body", False),
    "a_button.obj": ("A", False),
    "b_button.obj": ("B", False),
    "x_button.obj": ("X", False),
    "y_button.obj": ("Y", False),
    "back_button.obj": ("View", False),
    "start_button.obj": ("Menu", False),
    "guide_button.obj": ("Guide", False),
    "dpad_up.obj": ("DpadUp", False),
    "dpad_down.obj": ("DpadDown", False),
    "dpad_left.obj": ("DpadLeft", False),
    "dpad_right.obj": ("DpadRight", False),
    "left_bumper.obj": ("LB", False),
    "right_bumper.obj": ("RB", False),
    "left_trigger.obj": ("LT", False),
    "right_trigger.obj": ("RT", False),
    # stick + cap + ring 是同一个摇杆组件，全部随杆倾斜。
    "left_stick.obj": ("StickLeft", True),
    "left_cap.obj": ("StickLeft", True),
    "left_ring.obj": ("StickLeft", True),
    "right_stick.obj": ("StickRight", True),
    "right_cap.obj": ("StickRight", True),
    "right_ring.obj": ("StickRight", True),
}

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

GROUP_ORDER = [
    "Body", "A", "B", "X", "Y", "View", "Menu", "Guide",
    "DpadUp", "DpadDown", "DpadLeft", "DpadRight",
    "LB", "RB", "LT", "RT", "StickLeft", "StickRight",
]


def tx(p):
    return (p[0], -p[2], p[1])


def load_part(path):
    positions, normals, faces = [], [], []
    for raw in open(path, encoding="utf-8", errors="replace"):
        line = raw.strip()
        if line.startswith("v "):
            parts = line.split()
            positions.append((float(parts[1]), float(parts[2]), float(parts[3])))
        elif line.startswith("vn "):
            parts = line.split()
            normals.append((float(parts[1]), float(parts[2]), float(parts[3])))
        elif line.startswith("f "):
            corners = []
            for token in line.split()[1:]:
                bits = token.split("/")
                vi = int(bits[0]) - 1
                ni = int(bits[2]) - 1 if len(bits) >= 3 and bits[2] else -1
                corners.append((vi, ni))
            for i in range(1, len(corners) - 1):
                faces.append((corners[0], corners[i], corners[i + 1]))
    return positions, normals, faces


def main():
    src_dir, out_dir, out_name = sys.argv[1], sys.argv[2], sys.argv[3]
    info = json.load(open(os.path.join(src_dir, "info.json"), encoding="utf-8"))

    offsets = {}
    for part in info["parts"]:
        offsets[part["filename"]] = tuple(part["position"])
    # 摇杆组件（stick/cap/ring）共用同侧 ring 的装配偏移。
    for side in ("left", "right"):
        ring_pos = offsets[f"{side}_ring.obj"]
        for piece in ("stick", "cap", "ring"):
            offsets[f"{side}_{piece}.obj"] = ring_pos

    groups = {}
    for filename, (group, _) in PART_TO_GROUP.items():
        path = os.path.join(src_dir, filename)
        if not os.path.exists(path):
            continue
        positions, normals, faces = load_part(path)
        offset = offsets.get(filename, (0.0, 0.0, 0.0))
        groups.setdefault(group, []).append((positions, normals, faces, offset))

    obj_lines = [
        "# PadShift controller - merged from 3D Controller Overlay parts (MIT, Larf / Khyretos)",
        f"mtllib {out_name}.mtl",
    ]
    total_faces = 0
    vertex_base = 1
    normal_base = 1

    for group in GROUP_ORDER:
        pieces = groups.get(group)
        if not pieces:
            continue
        obj_lines.append(f"o {group}")
        obj_lines.append(f"usemtl {group}")
        for positions, normals, faces, offset in pieces:
            for p in positions:
                x, y, z = tx((p[0] + offset[0], p[1] + offset[1], p[2] + offset[2]))
                obj_lines.append(f"v {x:.6f} {y:.6f} {z:.6f}")
            for n in normals:
                x, y, z = tx(n)
                obj_lines.append(f"vn {x:.4f} {y:.4f} {z:.4f}")
            for (a_v, a_n), (b_v, b_n), (c_v, c_n) in faces:
                obj_lines.append(
                    f"f {a_v + vertex_base}//{a_n + normal_base} "
                    f"{b_v + vertex_base}//{b_n + normal_base} "
                    f"{c_v + vertex_base}//{c_n + normal_base}"
                )
                total_faces += 1
            vertex_base += len(positions)
            normal_base += len(normals)

    mtl_lines = [f"# PadShift {out_name} materials"]
    for group in GROUP_ORDER:
        if group not in groups:
            continue
        r, g, b = COLORS[group]
        mtl_lines.append(f"newmtl {group}")
        mtl_lines.append(f"Kd {r:.4f} {g:.4f} {b:.4f}")
        mtl_lines.append("Ks 0.4 0.4 0.4")
        mtl_lines.append("Ns 52")
        mtl_lines.append("")

    os.makedirs(out_dir, exist_ok=True)
    obj_path = os.path.join(out_dir, f"{out_name}.obj")
    mtl_path = os.path.join(out_dir, f"{out_name}.mtl")
    with open(obj_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(obj_lines) + "\n")
    with open(mtl_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(mtl_lines) + "\n")

    found = sorted(set(g for g in groups))
    print(f"groups: {found}")
    print(f"triangles: {total_faces}, obj bytes: {os.path.getsize(obj_path)}")


if __name__ == "__main__":
    main()
