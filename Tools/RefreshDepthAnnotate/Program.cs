using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Action_Wheel.Services;

if (args.Length is not (2 or 4))
    throw new ArgumentException("Usage: RefreshDepthAnnotate <input image> <output folder> [origin X origin Y]");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var original = new Bitmap(input);
using var capture = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
using (Graphics graphics = Graphics.FromImage(capture))
    graphics.DrawImageUnscaled(original, 0, 0);
int width = capture.Width, height = capture.Height;
int originX = args.Length == 4 ? int.Parse(args[2], CultureInfo.InvariantCulture) : (width - 1) / 2;
int originY = args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : (height - 1) / 2;
originX = Math.Clamp(originX, 0, width - 1);
originY = Math.Clamp(originY, 0, height - 1);
var bits = capture.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
byte[] bgra = new byte[checked(width * height * 4)];
try
{
    for (int y = 0; y < height; y++)
        Marshal.Copy(bits.Scan0 + y * bits.Stride, bgra, y * width * 4, width * 4);
}
finally { capture.UnlockBits(bits); }

// Use the application's real preprocessing, model and block sampling, without approximations.
RefreshDepthMap depth = RefreshDepthEstimator.Estimate(bgra, width, height);
var renderer = new UniformBlockRefreshRenderer(width, height, bgra, depth, originX, originY);
var blocks = renderer.Blocks.ToArray();
int cell = blocks.Max(block => block.CellWidth);
int columns = blocks.Select(block => block.CellLeft).Distinct().Count();
int rows = blocks.Select(block => block.CellTop).Distinct().Count();
int gridWidth = columns * cell, gridHeight = rows * cell;
var culture = CultureInfo.InvariantCulture;
var csv = new StringBuilder("column,row,cell_x,cell_y,cell_width,cell_height,core_x,core_y,core_width,core_height,sample_x,sample_y,depth,start_ms\n");
foreach (var block in blocks)
    csv.AppendLine(string.Join(",", new object[] {
        block.CellLeft / cell, block.CellTop / cell, block.CellLeft, block.CellTop,
        block.CellWidth, block.CellHeight, block.Left, block.Top, block.Width, block.Height,
        (block.CellLeft + block.CellWidth * .5).ToString("F1", culture),
        (block.CellTop + block.CellHeight * .5).ToString("F1", culture),
        block.Depth.ToString("R", culture), block.StartMs.ToString("F3", culture) }));
File.WriteAllText(Path.Combine(output, "blocks.csv"), csv.ToString(), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "depth.json"), JsonSerializer.Serialize(depth));

Render(false, 1, "depth-overlay.png");
Render(false, 2, "depth-overlay-2x.png");
Render(true, 2, "depth-grid-2x.png");
var metadata = new {
    Input = input, Width = width, Height = height, CellSize = cell, Columns = columns, Rows = rows,
    Blocks = blocks.Length, ModelWidth = depth.Width, ModelHeight = depth.Height,
    MinimumBlockDepth = blocks.Min(block => block.Depth), MaximumBlockDepth = blocks.Max(block => block.Depth),
    Meaning = "Relative inverse depth: 0 far, 1 near; cell centre sampled by actual renderer",
    OriginX = originX, OriginY = originY,
    OriginDepth = renderer.OriginDepth,
    OriginDefault = args.Length == 2 ? "Image centre; pass X Y to simulate a specific release" : "Explicit physical pixel release coordinates (clamped to image)",
    StartTime = "119 + 1590 * abs(depth - originDepth) / max(originDepth, 1 - originDepth) + 700 * normalizedScreenDistance milliseconds",
    TimingDepth = "Raw cell-centre depth compared to bilinear model depth at actual pointer pixel; same-depth cells farther from that pixel start later",
    ScreenDistance = "Cell-centre Euclidean distance to the release pixel centre, divided by distance from that pixel to the farthest monitor pixel centre; 1x1 uses zero",
    Padding = "Right/bottom partial cells are displayed as full slots; samples still use actual clipped centres",
    TimingVersion = "pointer-bidirectional-depth-distance-v3"
};
File.WriteAllText(Path.Combine(output, "metadata.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(metadata));
Console.WriteLine(output);

void Render(bool heatmap, int scale, string filename)
{
    const int header = 76, footer = 38;
    using var bitmap = new Bitmap(gridWidth * scale, (header + gridHeight + footer) * scale, PixelFormat.Format24bppRgb);
    using Graphics graphics = Graphics.FromImage(bitmap);
    graphics.ScaleTransform(scale, scale);
    graphics.Clear(Color.FromArgb(15, 20, 30));
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
    using var headingFont = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
    using var detailFont = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
    using var numberFont = new Font("Consolas", 10, FontStyle.Bold, GraphicsUnit.Pixel);
    using var white = new SolidBrush(Color.FromArgb(245, 247, 250));
    using var secondary = new SolidBrush(Color.FromArgb(181, 198, 217));
    graphics.DrawString(heatmap ? "DEPTH VALUES / BLOCK GRID" : "DEPTH VALUES / ORIGINAL IMAGE", headingFont, white, 14, 7);
    graphics.DrawString($"0.00 FAR  ->  1.00 NEAR   |   {width} x {height} image   |   {cell}px cells   |   {columns} x {rows} = {blocks.Length} blocks", detailFont, secondary, 16, 40);
    graphics.TranslateTransform(0, header);
    if (!heatmap)
    {
        graphics.DrawImage(capture, new Rectangle(0, 0, width, height));
        using var veil = new SolidBrush(Color.FromArgb(48, 0, 0, 0));
        graphics.FillRectangle(veil, 0, 0, width, height);
    }
    using var gridPen = new Pen(Color.FromArgb(heatmap ? 95 : 85, 210, 232, 245), .55f);
    using var corePen = new Pen(Color.FromArgb(heatmap ? 80 : 110, 97, 226, 244), .6f);
    using var textOutline = new Pen(Color.FromArgb(235, 5, 10, 19), 1.7f) { LineJoin = LineJoin.Round };
    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    foreach (var block in blocks)
    {
        if (heatmap)
        {
            using var fill = new SolidBrush(DepthColour(block.Depth));
            graphics.FillRectangle(fill, block.CellLeft, block.CellTop, cell, cell);
        }
        graphics.DrawRectangle(gridPen, block.CellLeft, block.CellTop, cell, cell);
        if (!heatmap)
            graphics.DrawRectangle(corePen, block.Left, block.Top, block.Width, block.Height);
        // Full label slots preserve legibility for the clipped final row/column.
        using var path = new GraphicsPath();
        path.AddString(block.Depth.ToString("F2", culture), numberFont.FontFamily, (int)FontStyle.Bold,
            10, new RectangleF(block.CellLeft, block.CellTop, cell, cell), format);
        graphics.DrawPath(textOutline, path);
        graphics.FillPath(white, path);
    }
    using var edgePen = new Pen(Color.FromArgb(245, 185, 75), 1f) { DashStyle = DashStyle.Dash };
    graphics.DrawLine(edgePen, width, 0, width, height);
    graphics.DrawLine(edgePen, 0, height, width, height);
    graphics.DrawString("Numbers = centre-sampled depth, rounded to 0.01. Dashed edge = original image boundary; partial edge-cell labels extend into padding.",
        detailFont, secondary, 14, gridHeight + 8);
    bitmap.Save(Path.Combine(output, filename), ImageFormat.Png);
}

static Color DepthColour(float value)
{
    Color[] stops = [Color.FromArgb(27, 40, 93), Color.FromArgb(27, 111, 150),
        Color.FromArgb(37, 177, 161), Color.FromArgb(184, 207, 86), Color.FromArgb(247, 213, 90)];
    float position = Math.Clamp(value, 0, 1) * (stops.Length - 1);
    int lower = Math.Min(stops.Length - 2, (int)position);
    float fraction = position - lower;
    int Channel(int a, int b) => (int)MathF.Round(a + (b - a) * fraction);
    return Color.FromArgb(Channel(stops[lower].R, stops[lower + 1].R),
        Channel(stops[lower].G, stops[lower + 1].G), Channel(stops[lower].B, stops[lower + 1].B));
}
