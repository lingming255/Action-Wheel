using Action_Wheel.Core;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

// Parent -> first child stays in the same main-ring sector: every movement must be hit-tested.
Check(RingGeometry.TagForDirection(0, -100) == RingGeometry.TagForDirection(0, -200),
    "Parent and child can share a direction");
Check(RingGeometry.GroupChildAt(0, -100, 1, 1, 8, 200, 52) == null,
    "Parent is not a child");
Check(RingGeometry.GroupChildAt(0, -200, 1, 1, 8, 200, 52) == 0,
    "Moving outward along the same direction selects child zero");

foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
foreach (int parent in Enumerable.Range(1, 8))
foreach (int count in new[] { 1, 3, 8 })
{
    for (int i = 0; i < count; i++)
    {
        var centre = RingGeometry.GroupChildOffset(parent, i, 200, 52);
        Check(RingGeometry.GroupChildAt(Math.Round(centre.X * scale),
            Math.Round(centre.Y * scale), scale, parent, count, 200, 52) == i,
            $"Child {parent}:{i} hit at {scale * 100}% DPI");
    }
    Check(RingGeometry.GroupChildAt(0, 0, scale, parent, count, 200, 52) == null,
        "Returning to the centre clears child selection");
}

// The circle's bounding-box corners and the gap between adjacent children are not buttons.
Check(RingGeometry.GroupChildAt(25, -175, 1, 1, 1, 200, 52) == null,
    "Transparent corner must not dispatch an action");
var first = RingGeometry.GroupChildOffset(1, 0, 200, 52);
var second = RingGeometry.GroupChildOffset(1, 1, 200, 52);
Check(RingGeometry.GroupChildAt((first.X + second.X) / 2, (first.Y + second.Y) / 2,
    1, 1, 8, 200, 52) == null, "Moving between children clears selection in the gap");
var sibling = RingGeometry.GroupChildOffset(1, 7, 200, 52);
Check(RingGeometry.TagForDirection(sibling.X, sibling.Y) != 1,
    "An outer sibling crosses a main-ring sector and must keep the original group open");
Console.WriteLine($"PASS: {checks} ring gesture geometry checks");
