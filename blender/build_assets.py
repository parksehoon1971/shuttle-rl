"""
Shuttle rack modular asset builder for Blender (tested with Blender/bpy 5.1).

Run from Blender:   blender --background --python build_assets.py -- <out_dir>
or inside Blender's Scripting tab (outputs next to the .blend / script).

Creates one FBX per module plus dimensions.json, which the Unity rack generator
reads so that the Unity layout and the 3D models always use the same numbers.

Coordinate convention (Blender): X = main aisle direction, Y = lane direction,
Z = up. Units are meters. Z = 0 is the "rail datum" of a level, i.e. the bottom
of the rail where shuttle wheels run.

Structure (see docs/lift_shuttle_design.md):
  * Shuttle = 4-way pallet shuttle. Two wheel sets (WheelsX for the aisle,
    WheelsY for lanes), a Jack plate that lifts the pallet off the rail ledge.
    Height below the ledge, so an empty shuttle can drive under stored pallets.
  * Rails = C-profile: inner bottom flange is the wheel track, outer top ledge
    carries the pallet edges.
  * RollerRail = same profile, but the top ledge is a row of driven rollers.
    Used for the per-level P&D (transfer) station and for the lift carriage.
    Because the wheel track is continuous, a shuttle can later drive onto the
    P&D station and onto the lift carriage (future: shuttle rides the lift).
"""
import bpy, bmesh, json, math, os, sys

# ----------------------------------------------------------------------------
# Dimensions (meters). Change here; Unity reads dimensions.json.
# ----------------------------------------------------------------------------
D = {
    "pallet": {"w": 1.10, "d": 1.10, "h": 0.15, "load_h": 1.00},
    "rail": {
        "gauge_half": 0.495,     # web center offset from module centerline
        "web_t": 0.010, "height": 0.20,
        "track_w": 0.065, "track_t": 0.010,   # inner bottom flange (wheel track)
        "ledge_w": 0.080, "ledge_t": 0.010,   # outer top ledge (pallet support)
    },
    "cell": {"pitch_lane": 1.30, "pitch_aisle": 1.40},  # cell length along lane, lane pitch along aisle
    "level": {"height": 1.60},
    "shuttle": {"w": 0.96, "l": 0.96, "body_bottom": 0.03, "body_top": 0.18,
                 "jack_t": 0.010, "jack_stroke": 0.040, "wheel_r": 0.040,
                 "wheel_shift": 0.015},
    "roller": {"r": 0.030, "pitch": 0.100, "len": 0.080},
    "lift": {"carriage_l": 1.40, "carriage_w": 1.30, "mast_offset": 0.78},
    "conveyor": {"l": 1.30, "w": 1.20},
    "upright": {"size": 0.10},
}

COLORS = {
    "steel": (0.55, 0.57, 0.60, 1), "rail": (0.20, 0.35, 0.65, 1),
    "upright": (0.15, 0.30, 0.60, 1), "beam": (0.90, 0.45, 0.10, 1),
    "shuttle": (0.95, 0.75, 0.10, 1), "shuttle_dark": (0.15, 0.15, 0.17, 1),
    "jack": (0.35, 0.35, 0.38, 1), "wheel": (0.08, 0.08, 0.08, 1),
    "light": (0.10, 0.90, 0.30, 1), "wood": (0.72, 0.55, 0.33, 1),
    "load": (0.78, 0.66, 0.48, 1), "roller": (0.70, 0.72, 0.75, 1),
    "lift": (0.85, 0.20, 0.15, 1), "conveyor": (0.30, 0.30, 0.32, 1),
}

# ----------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------
_mats = {}
def mat(name):
    if name in _mats:
        return _mats[name]
    m = bpy.data.materials.new("M_" + name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = COLORS[name]
        bsdf.inputs["Metallic"].default_value = 0.6 if name in ("steel", "roller", "jack") else 0.1
        bsdf.inputs["Roughness"].default_value = 0.45
    m.diffuse_color = COLORS[name]
    _mats[name] = m
    return m

def _link(obj, coll, parent):
    coll.objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj

def empty(name, coll, parent=None, loc=(0, 0, 0)):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.2
    e.location = loc
    return _link(e, coll, parent)

def box(name, size, center, color, coll, parent=None):
    """Axis-aligned box. size=(x,y,z), center in parent space."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= size[0]; v.co.y *= size[1]; v.co.z *= size[2]
    bm.to_mesh(me); bm.free()
    me.materials.append(mat(color))
    ob = bpy.data.objects.new(name, me)
    ob.location = center
    return _link(ob, coll, parent)

def cyl(name, r, length, center, axis, color, coll, parent=None, seg=16):
    """Cylinder along axis 'X' | 'Y' | 'Z'."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r, radius2=r, depth=length)
    rot = {"X": (0, math.pi / 2, 0), "Y": (math.pi / 2, 0, 0), "Z": (0, 0, 0)}[axis]
    from mathutils import Euler
    bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Euler(rot).to_matrix())
    bm.to_mesh(me); bm.free()
    me.materials.append(mat(color))
    for p in me.polygons:
        p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    ob.location = center
    return _link(ob, coll, parent)

def new_asset(name):
    coll = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(coll)
    root = empty(name, coll)
    return coll, root

# ----------------------------------------------------------------------------
# Rail profile
# ----------------------------------------------------------------------------
def rail_pair(prefix, length, along, coll, parent, roller_top=False, ledge=True):
    """Two C-rails running along 'X' or 'Y', centered on the module."""
    R = D["rail"]
    g, H = R["gauge_half"], R["height"]
    for side in (-1, 1):
        sname = "L" if side < 0 else "R"
        def place(off, z, w, h, nm, col="rail"):
            # off = lateral offset from centerline, w = lateral width, h = height
            if along == "Y":
                return box(f"{prefix}_{nm}_{sname}", (w, length, h), (off, 0, z), col, coll, parent)
            return box(f"{prefix}_{nm}_{sname}", (length, w, h), (0, off, z), col, coll, parent)
        # web
        place(side * g, H / 2, R["web_t"], H, "Web")
        # wheel track (inner bottom flange)
        place(side * (g - R["track_w"] / 2), R["track_t"] / 2, R["track_w"], R["track_t"], "Track")
        if roller_top:
            # roller row on the outer top: rollers convey along the rail
            n = max(1, int(round(length / D["roller"]["pitch"])))
            rr = D["roller"]["r"]
            off = side * (g + R["ledge_w"] / 2)
            place(off, H - rr - 0.012, R["ledge_w"] + 0.02, 0.012, "RollerFrame", "steel")
            for i in range(n):
                s = -length / 2 + (i + 0.5) * length / n
                if along == "Y":
                    c, ax = (off, s, H - rr), "X"
                else:
                    c, ax = (s, off, H - rr), "Y"
                cyl(f"{prefix}_Roller_{sname}_{i:02d}", rr, D["roller"]["len"], c, ax, "roller", coll, parent, seg=12)
        elif ledge:
            place(side * (g + R["ledge_w"] / 2), H - R["ledge_t"] / 2, R["ledge_w"], R["ledge_t"], "Ledge")

# ----------------------------------------------------------------------------
# Assets
# ----------------------------------------------------------------------------
def build_pallet():
    P = D["pallet"]
    coll, root = new_asset("Pallet")
    w, d, h = P["w"], P["d"], P["h"]
    deck_t, bot_t = 0.022, 0.022
    blk_h = h - deck_t - bot_t
    for i, y in enumerate((-0.46, 0, 0.46)):  # top deck boards
        box(f"Deck_{i}", (w, 0.16, deck_t), (0, y, h - deck_t / 2), "wood", coll, root)
    for i, x in enumerate((-0.30, -0.10, 0.10, 0.30)):
        box(f"DeckThin_{i}", (0.10, d, deck_t * 0.8), (x, 0, h - deck_t - deck_t * 0.4), "wood", coll, root)
    k = 0
    for x in (-0.50, 0, 0.50):
        for y in (-0.50, 0, 0.50):
            box(f"Block_{k}", (0.10, 0.10, blk_h), (x, y, bot_t + blk_h / 2), "wood", coll, root); k += 1
    for i, x in enumerate((-0.50, 0, 0.50)):  # bottom runners (span across both rails)
        box(f"Runner_{i}", (0.10, d, bot_t), (x, 0, bot_t / 2), "wood", coll, root)
    return root

def build_load():
    P = D["pallet"]
    coll, root = new_asset("PalletLoad")
    box("Load", (P["w"] - 0.04, P["d"] - 0.04, P["load_h"]), (0, 0, P["load_h"] / 2), "load", coll, root)
    return root

def build_shuttle():
    S, R = D["shuttle"], D["rail"]
    coll, root = new_asset("Shuttle")
    body = empty("Body", coll, root)
    bh = S["body_top"] - S["body_bottom"]
    box("Chassis", (S["l"], S["w"], bh), (0, 0, S["body_bottom"] + bh / 2), "shuttle", coll, body)
    for sx in (-1, 1):  # bumpers front/back along X and Y
        box(f"BumperX_{sx}", (0.02, S["w"] * 0.8, 0.06), (sx * (S["l"] / 2 + 0.01), 0, 0.10), "shuttle_dark", coll, body)
        box(f"BumperY_{sx}", (S["l"] * 0.8, 0.02, 0.06), (0, sx * (S["w"] / 2 + 0.01), 0.10), "shuttle_dark", coll, body)
    box("BatteryCover", (0.40, 0.30, 0.004), (0.20, 0, S["body_top"] + 0.002), "shuttle_dark", coll, body)
    sl = empty("StatusLight", coll, root)
    cyl("Lamp", 0.02, 0.03, (-0.40, 0.40, S["body_top"] + 0.015), "Z", "light", coll, sl)
    # sensors (front/back for each direction) as empties for Unity raycasts
    for nm, loc in (("Sensor_PX", (S["l"] / 2 + 0.02, 0, 0.1)), ("Sensor_NX", (-S["l"] / 2 - 0.02, 0, 0.1)),
                    ("Sensor_PY", (0, S["w"] / 2 + 0.02, 0.1)), ("Sensor_NY", (0, -S["w"] / 2 - 0.02, 0.1))):
        empty(nm, coll, root, loc)
    # Jack plate (Unity animates +Z by jack_stroke)
    jack = empty("Jack", coll, root)
    box("JackPlate", (0.86, 0.86, S["jack_t"]), (0, 0, S["body_top"] + S["jack_t"] / 2), "jack", coll, jack)
    # wheel sets: wheels ride on the rail track (top at track_t)
    r = S["wheel_r"]; zc = R["track_t"] + r
    wy = empty("WheelsY", coll, root)   # lane direction (rolls along Y), sits under X-sides
    wx = empty("WheelsX", coll, root)   # aisle direction (rolls along X)
    lat = R["gauge_half"] - R["track_w"] / 2  # over the track
    for i, (sx, sy) in enumerate(((-1, -1), (-1, 1), (1, -1), (1, 1))):
        cyl(f"WheelY_{i}", r, 0.03, (sx * lat, sy * 0.30, zc), "X", "wheel", coll, wy)
        cyl(f"WheelX_{i}", r, 0.03, (sx * 0.30, sy * lat, zc), "Y", "wheel", coll, wx)
    return root

def build_lane_rail():
    coll, root = new_asset("LaneRail")
    rail_pair("LR", D["cell"]["pitch_lane"], "Y", coll, root)
    return root

def build_aisle_rail():
    coll, root = new_asset("AisleRail")
    rail_pair("AR", D["cell"]["pitch_aisle"], "X", coll, root, ledge=False)
    return root

def build_junction():
    """Aisle/lane crossing cell: wheel-track plate in both directions, corner posts."""
    R = D["rail"]
    coll, root = new_asset("Junction")
    L = D["cell"]["pitch_aisle"]
    g = R["gauge_half"]
    box("TrackPlate", (2 * g + 0.02, 2 * g + 0.02, R["track_t"]), (0, 0, R["track_t"] / 2), "rail", coll, root)
    # hollow centre visual: dark inset
    box("Inset", (2 * (g - R["track_w"]), 2 * (g - R["track_w"]), R["track_t"] + 0.001), (0, 0, R["track_t"] / 2), "steel", coll, root)
    for i, (sx, sy) in enumerate(((-1, -1), (-1, 1), (1, -1), (1, 1))):
        box(f"CornerWeb_{i}", (0.06, 0.06, R["height"]), (sx * (g + 0.03), sy * (g + 0.03), R["height"] / 2), "rail", coll, root)
    return root

def build_pd_station():
    coll, root = new_asset("PDStation")
    rail_pair("PD", D["lift"]["carriage_l"], "X", coll, root, roller_top=True)
    box("DriveMotor", (0.20, 0.12, 0.12), (0, -(D["rail"]["gauge_half"] + 0.20), 0.08), "steel", coll, root)
    empty("PalletSensor", coll, root, (0, 0, D["rail"]["height"] + 0.05))
    return root

def build_lift_carriage():
    Lf, R = D["lift"], D["rail"]
    coll, root = new_asset("LiftCarriage")
    box("Platform", (Lf["carriage_l"], Lf["carriage_w"], 0.05), (0, 0, -0.025), "lift", coll, root)
    rail_pair("LC", Lf["carriage_l"], "X", coll, root, roller_top=True)
    for i, sy in enumerate((-1, 1)):
        box(f"GuideShoe_{i}", (0.20, 0.08, 0.40), (0, sy * (Lf["mast_offset"] - 0.06), 0.15), "lift", coll, root)
    # flip-up stops at both conveying ends (Unity toggles)
    for nm, sx in (("Stop_PX", 1), ("Stop_NX", -1)):
        s = empty(nm, coll, root, (sx * (Lf["carriage_l"] / 2 - 0.02), 0, 0))
        for side in (-1, 1):
            box(f"{nm}_Flag_{'L' if side < 0 else 'R'}", (0.02, 0.06, 0.08), (0, side * (R["gauge_half"] + 0.04), R["height"] + 0.04), "beam", coll, s)
    empty("PalletSensor", coll, root, (0, 0, R["height"] + 0.05))
    empty("ShuttleLock", coll, root, (0, 0, 0.05))  # future: shuttle riding the lift
    return root

def build_lift_mast():
    Lf = D["lift"]; H = D["level"]["height"]
    coll, root = new_asset("LiftMastSegment")
    for i, sy in enumerate((-1, 1)):
        box(f"Column_{i}", (0.16, 0.10, H), (0, sy * Lf["mast_offset"], H / 2), "steel", coll, root)
        box(f"GuideRail_{i}", (0.04, 0.03, H), (0, sy * (Lf["mast_offset"] - 0.065), H / 2), "steel", coll, root)
    box("TieBeam", (0.10, 2 * Lf["mast_offset"], 0.06), (-0.10, 0, H - 0.03), "steel", coll, root)
    return root

def build_conveyor():
    C = D["conveyor"]; R = D["rail"]
    coll, root = new_asset("Conveyor")
    rr = D["roller"]["r"]
    top = R["height"]
    for sy in (-1, 1):
        box(f"SideFrame_{sy}", (C["l"], 0.04, 0.12), (0, sy * (C["w"] / 2 + 0.02), top - 0.06), "conveyor", coll, root)
    n = int(round(C["l"] / D["roller"]["pitch"]))
    for i in range(n):
        x = -C["l"] / 2 + (i + 0.5) * C["l"] / n
        cyl(f"Roller_{i:02d}", rr, C["w"], (x, 0, top - rr), "Y", "roller", coll, root, seg=12)
    for sx in (-1, 1):
        for sy in (-1, 1):
            box(f"Leg_{sx}_{sy}", (0.05, 0.05, top - 0.12), (sx * (C["l"] / 2 - 0.05), sy * (C["w"] / 2), (top - 0.12) / 2), "conveyor", coll, root)
    return root

def build_upright():
    U = D["upright"]["size"]; H = D["level"]["height"]
    coll, root = new_asset("Upright")
    box("Column", (U, U, H), (0, 0, H / 2), "upright", coll, root)
    return root

def build_beam():
    """Cross beam along X between uprights, one per lane pitch; supports the lane rails."""
    coll, root = new_asset("Beam")
    box("Beam", (D["cell"]["pitch_aisle"], 0.05, 0.10), (0, 0, -0.05), "beam", coll, root)
    return root

BUILDERS = [build_pallet, build_load, build_shuttle, build_lane_rail, build_aisle_rail,
            build_junction, build_pd_station, build_lift_carriage, build_lift_mast,
            build_conveyor, build_upright, build_beam]

# ----------------------------------------------------------------------------
# Export
# ----------------------------------------------------------------------------
def select_tree(root):
    bpy.ops.object.select_all(action="DESELECT")
    stack = [root]
    while stack:
        o = stack.pop()
        o.select_set(True)
        stack.extend(o.children)
    bpy.context.view_layer.objects.active = root

def export_fbx(root, out_dir):
    select_tree(root)
    path = os.path.join(out_dir, root.name + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"EMPTY", "MESH"},
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False)
    return path

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out_dir = argv[0] if argv else os.path.join(os.path.dirname(bpy.data.filepath or __file__), "export")
    os.makedirs(out_dir, exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    roots = [b() for b in BUILDERS]
    for r in roots:
        print("exported", export_fbx(r, out_dir))
    dims = dict(D)
    dims["axis_note"] = "Blender X=aisle, Y=lane, Z=up. Unity (after FBX import): X=aisle, Z=lane, Y=up."
    with open(os.path.join(out_dir, "dimensions.json"), "w") as f:
        json.dump(dims, f, indent=2)
    blend = os.path.join(out_dir, "shuttle_rack_assets.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    print("saved", blend)

if __name__ == "__main__":
    main()
