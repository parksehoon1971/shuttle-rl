"""
Builds a small sample rack from the modules in build_assets.py and renders a preview PNG.
Run:  blender --background --python preview_scene.py -- <out.png>
Sample: 2 levels, 1 main aisle, 3 lanes per side, 3 cells per lane, 1 lift + P&D per level.
"""
import bpy, math, os, sys, importlib.util

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("build_assets", os.path.join(here, "build_assets.py"))
A = importlib.util.module_from_spec(spec); spec.loader.exec_module(A)
D = A.D

bpy.ops.wm.read_factory_settings(use_empty=True)
roots = {b().name: b for b in A.BUILDERS}  # builds every asset into its own collection
scene = bpy.context.scene
lib = {c.name: c for c in bpy.data.collections}
# hide source assets from render: move them out of the scene, use instances
for c in list(scene.collection.children):
    scene.collection.children.unlink(c)
layout = bpy.data.collections.new("Layout"); scene.collection.children.link(layout)

def inst(name, loc, rot_z=0.0, scale=(1, 1, 1)):
    e = bpy.data.objects.new(f"I_{name}", None)
    e.instance_type = "COLLECTION"; e.instance_collection = lib[name]
    e.location = loc; e.rotation_euler = (0, 0, rot_z); e.scale = scale
    layout.objects.link(e); return e

LEVELS, LANES, CELLS = 2, 3, 3
H = D["level"]["height"]; PA = D["cell"]["pitch_aisle"]; PL = D["cell"]["pitch_lane"]
g = D["rail"]["gauge_half"]; y0 = g + 0.01
base = 0.0
occupied = {(0, 0, 1, 2), (0, 1, -1, 0), (1, 2, 1, 1), (1, 0, -1, 2), (0, 2, 1, 0)}
for lv in range(LEVELS):
    z = base + lv * H
    for k in range(LANES):
        x = k * PA
        inst("Junction", (x, 0, z))
        if k < LANES - 1:
            inst("AisleRail", (x + PA / 2, 0, z), scale=((PA - 2 * y0) / PA, 1, 1))
        for side in (-1, 1):
            for i in range(CELLS):
                y = side * (y0 + (i + 0.5) * PL)
                inst("LaneRail", (x, y, z))
                inst("Beam", (x, y + side * PL / 2, z))
                if (lv, k, side, i) in occupied:
                    inst("Pallet", (x, y, z + D["rail"]["height"]))
                    inst("PalletLoad", (x, y, z + D["rail"]["height"] + D["pallet"]["h"]))
            for i in range(CELLS + 1):
                for dx in ((-PA / 2, PA / 2) if k == LANES - 1 else (-PA / 2,)):
                    inst("Upright", (x + dx, side * (y0 + i * PL), z - base if lv == 0 else z))
    # P&D station and lift at aisle start (-X)
    inst("PDStation", (-PA / 2 - D["lift"]["carriage_l"] / 2, 0, z))
    xl = -PA / 2 - D["lift"]["carriage_l"] * 1.5
    inst("LiftMastSegment", (xl, 0, (z - base) if lv == 0 else z), )
# lift carriage at level 1, carrying a pallet
xl = -PA / 2 - D["lift"]["carriage_l"] * 1.5
zc = base + 1 * H
inst("LiftCarriage", (xl, 0, zc))
inst("Pallet", (xl, 0, zc + D["rail"]["height"]))
inst("PalletLoad", (xl, 0, zc + D["rail"]["height"] + D["pallet"]["h"]))
inst("LiftMastSegment", (xl, 0, base + LEVELS * H - base))
# inbound conveyor at floor feeding the lift at level 0
for j in range(2):
    inst("Conveyor", (xl - D["lift"]["carriage_l"] / 2 - D["conveyor"]["l"] * (j + 0.5), 0, base))
# shuttles: one loaded in the aisle, one empty driving under a stored pallet
z0 = base
S = inst("Shuttle", (PA, 0, z0))
inst("Pallet", (PA, 0, z0 + D["rail"]["height"] + 0.03))
inst("PalletLoad", (PA, 0, z0 + D["rail"]["height"] + 0.03 + D["pallet"]["h"]))
inst("Shuttle", (0, y0 + 2.5 * PL, z0))  # under pallet at (0,0,+1,2)
inst("Shuttle", (2 * PA, -(y0 + 0.5 * PL), base + H))

# floor
me = bpy.data.meshes.new("Floor"); me.from_pydata([(-9, -7, -0.005), (6, -7, -0.005), (6, 7, -0.005), (-9, 7, -0.005)], [], [(0, 1, 2, 3)])
fl = bpy.data.objects.new("Floor", me); layout.objects.link(fl)
fm = bpy.data.materials.new("Floor"); fm.diffuse_color = (0.82, 0.83, 0.85, 1); me.materials.append(fm)

cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam")); layout.objects.link(cam)
cam.location = (7.5, -8.0, 5.2)
cam.rotation_euler = (math.radians(66), 0, math.radians(40))
cam.data.lens = 30
scene.camera = cam
scene.render.engine = "CYCLES"          # CPU path tracer: works headless without a GPU
scene.cycles.device = "CPU"
scene.cycles.samples = 24
scene.cycles.use_denoising = True
scene.view_settings.view_transform = "Standard"
sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN")); layout.objects.link(sun)
sun.data.energy = 2.2; sun.rotation_euler = (math.radians(40), math.radians(10), math.radians(30))
world = bpy.data.worlds.new("World"); scene.world = world; world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.9, 0.92, 0.95, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
fm.use_nodes = True
scene.render.resolution_x, scene.render.resolution_y = 1600, 1000
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out = argv[0] if argv else os.path.join(here, "preview.png")
scene.render.filepath = out
bpy.ops.render.render(write_still=True)
print("rendered", out)
