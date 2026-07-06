using System.Numerics;
using ARoomReborn;

var failures = 0;

void Check(bool condition, string name)
{
    System.Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
    if (!condition)
        failures++;
}

static FurnitureRecord R(uint id, float x = 0, float y = 0, float z = 0, float rot = 0, byte stain = 0)
    => new(id, id, new Vector3(x, y, z), rot, stain);

static Dictionary<int, FurnitureRecord> D(params (int index, FurnitureRecord rec)[] items)
{
    var d = new Dictionary<int, FurnitureRecord>();
    foreach (var (index, rec) in items)
        d[index] = rec;
    return d;
}

// Placed: appears in new only.
{
    var c = LayoutDiffer.Diff(D(), D((1, R(100, 1, 2, 3))));
    Check(c.Count == 1 && c[0].Action == HistoryAction.Placed && c[0].Index == 1, "placed");
}

// Removed: gone from new.
{
    var c = LayoutDiffer.Diff(D((1, R(100))), D());
    Check(c.Count == 1 && c[0].Action == HistoryAction.Removed, "removed");
}

// Moved: position changed beyond epsilon.
{
    var c = LayoutDiffer.Diff(D((1, R(100, 0, 0, 0))), D((1, R(100, 1, 0, 0))));
    Check(c.Count == 1 && c[0].Action == HistoryAction.Moved, "moved");
}

// Sub-epsilon jitter is ignored.
{
    var c = LayoutDiffer.Diff(D((1, R(100, 0, 0, 0))), D((1, R(100, 0.001f, 0, 0))));
    Check(c.Count == 0, "sub-epsilon jitter ignored");
}

// Rotation only.
{
    var c = LayoutDiffer.Diff(D((1, R(100, 0, 0, 0, 0f))), D((1, R(100, 0, 0, 0, 1f))));
    Check(c.Count == 1 && c[0].Action == HistoryAction.Rotated, "rotated");
}

// Dye only.
{
    var c = LayoutDiffer.Diff(D((1, R(100, 0, 0, 0, 0f, 1))), D((1, R(100, 0, 0, 0, 0f, 2))));
    Check(c.Count == 1 && c[0].Action == HistoryAction.Redyed, "redyed");
}

// Position change wins when both move and dye changed.
{
    var c = LayoutDiffer.Diff(D((1, R(100, 0, 0, 0, 0f, 1))), D((1, R(100, 5, 0, 0, 0f, 2))));
    Check(c.Count == 1 && c[0].Action == HistoryAction.Moved, "move beats dye");
}

// Slot reuse: same index, different furnishing id -> removed + placed.
{
    var c = LayoutDiffer.Diff(D((1, R(100))), D((1, R(200))));
    Check(c.Count == 2 && c.Exists(x => x.Action == HistoryAction.Removed) && c.Exists(x => x.Action == HistoryAction.Placed), "slot reuse");
}

// Identical snapshots produce nothing.
{
    var c = LayoutDiffer.Diff(D((1, R(100, 1, 2, 3, 4, 5))), D((1, R(100, 1, 2, 3, 4, 5))));
    Check(c.Count == 0, "identical -> no change");
}

// Same item, same everything, but a different slot index (outdoor reindex noise): silent.
{
    var c = LayoutDiffer.Diff(D((101, R(100, 5, 5, 5))), D((105, R(100, 5, 5, 5))));
    Check(c.Count == 0, "reindex with no real change is silent");
}

// Same item actually moved, and its slot index changed too: one Moved, not remove+place.
{
    var c = LayoutDiffer.Diff(D((101, R(100, 0, 0, 0))), D((105, R(100, 5, 0, 0))));
    Check(c.Count == 1 && c[0].Action == HistoryAction.Moved && c[0].Index == 105, "moved item that also got reindexed");
}

// Two identical items swap slot indices without moving: silent, not a false move.
{
    var oldS = D((101, R(100, 1, 1, 1)), (102, R(100, 9, 9, 9)));
    var newS = D((201, R(100, 9, 9, 9)), (202, R(100, 1, 1, 1)));
    var c = LayoutDiffer.Diff(oldS, newS);
    Check(c.Count == 0, "two identical items reindexing without moving stays silent");
}

System.Console.WriteLine();
System.Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
