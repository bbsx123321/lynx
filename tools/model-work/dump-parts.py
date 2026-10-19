"""列出 blend 中全部网格对象的三角形数与局部/世界包围盒，用于确认控件方位。"""
import sys

import bpy


def main():
    path = sys.argv[1]
    bpy.ops.wm.open_mainfile(filepath=path)
    deps = bpy.context.evaluated_depsgraph_get()

    rows = []
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        evaluated = obj.evaluated_get(deps)
        mesh = evaluated.to_mesh()
        tris = sum(len(p.vertices) - 2 for p in mesh.polygons)
        evaluated.to_mesh_clear()

        world = obj.matrix_world
        centre = world.translation
        # 世界空间的近似包围盒中心（缩放均匀，直接用位移+局部中点）
        cos = [world @ v.co for v in obj.data.vertices]
        cx = (min(c.x for c in cos) + max(c.x for c in cos)) / 2
        cy = (min(c.y for c in cos) + max(c.y for c in cos)) / 2
        cz = (min(c.z for c in cos) + max(c.z for c in cos)) / 2
        rows.append((obj.name, tris, cx, cy, cz))

    # Blend 的 Z 朝上、-Y 朝前（面对相机）。按 (cx, cy) 排序输出便于人工判读方位。
    for name, tris, cx, cy, cz in sorted(rows, key=lambda r: (r[3], r[2])):
        print(f"{name:26s} tris={tris:7d}  centre=({cx:8.4f},{cy:8.4f},{cz:8.4f})")


if __name__ == "__main__":
    main()
