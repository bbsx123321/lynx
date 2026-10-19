"""分析 blend 源文件中摇杆对象的几何形状：半径-高度剖面 + 顶点统计。

用法: python analyze-sticks.py <model.blend> [对象名 ...]
不传对象名时自动匹配包含 Thumb/Stick 的对象。
"""
import math
import sys

import bpy
from mathutils import Vector


def world_tri_count(obj, deps):
    evaluated = obj.evaluated_get(deps)
    mesh = evaluated.to_mesh()
    count = sum(len(p.vertices) - 2 for p in mesh.polygons)
    evaluated.to_mesh_clear()
    return count


def analyze(obj, deps):
    evaluated = obj.evaluated_get(deps)
    mesh = evaluated.to_mesh()

    # 局部坐标分析网格自身形状（不受世界缩放/位移影响）
    cos = [Vector(v.co) for v in mesh.vertices]
    cx = (min(c.x for c in cos) + max(c.x for c in cos)) / 2
    cy = (min(c.y for c in cos) + max(c.y for c in cos)) / 2

    span_x = max(c.x for c in cos) - min(c.x for c in cos)
    span_z = max(c.z for c in cos) - min(c.z for c in cos)
    quant = max(span_x / 24, 1e-6)

    layers = {}
    for c in cos:
        r = math.hypot(c.x - cx, c.y - cy)
        rq = round(r / quant) * quant
        layers.setdefault(round(rq, 4), []).append(c.z)

    print(f"=== {obj.name} ===")
    print(f"  verts={len(cos)} tris={world_tri_count(obj, deps)}")
    print(f"  local bounds: X[{min(c.x for c in cos):.4f},{max(c.x for c in cos):.4f}] "
          f"Y[{min(c.y for c in cos):.4f},{max(c.y for c in cos):.4f}] "
          f"Z[{min(c.z for c in cos):.4f},{max(c.z for c in cos):.4f}]  (spanX={span_x:.4f} spanZ={span_z:.4f})")
    print(f"  world matrix scale: {obj.matrix_world.to_scale()}")
    print("  local radius -> Z profile:")
    for rq in sorted(layers):
        zs_r = layers[rq]
        bar = "#" * min(len(zs_r) // 8, 40)
        print(f"    r={rq:9.4f}: {len(zs_r):4d} verts  Z[{min(zs_r):9.4f} .. {max(zs_r):9.4f}] {bar}")
    evaluated.to_mesh_clear()


def main():
    path = sys.argv[1]
    targets = sys.argv[2:]
    bpy.ops.wm.open_mainfile(filepath=path)
    deps = bpy.context.evaluated_depsgraph_get()

    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        if targets:
            if obj.name in targets:
                analyze(obj, deps)
        elif any(k in obj.name.lower() for k in ("thumb", "stick")):
            analyze(obj, deps)


if __name__ == "__main__":
    main()
