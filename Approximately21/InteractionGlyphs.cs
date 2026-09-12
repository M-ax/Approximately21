using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Approximately21;

// Atlas-free 3x5 glyphs, cached as a mesh by each placed control until its text changes.
internal static class InteractionGlyphs
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.:/!?()$= ";
    private static readonly string[] Rows =
    {
        "25755", "65656", "34443", "65556", "74647", "74644", "34553", "55755", "72227",
        "11153", "55655", "44447", "57755", "57555", "25552", "65644", "25573", "65655",
        "34216", "72222", "55557", "55552", "55575", "55255", "55222", "71247",
        "75557", "26227", "71247", "71217", "55711", "74617", "74657", "71111", "75757", "75717",
        "02720", "00700", "00002", "02020", "11244", "22202", "71202", "24442", "21112", "37636", "07070", "00000"
    };

    internal static Mesh Create(InteractionButton button, string text)
    {
        text = (text ?? string.Empty).ToUpperInvariant();
        if (text.Length > 128) text = text.Substring(0, 128);
        var lines = text.Split('\n');
        var columns = 1;
        foreach (var line in lines) columns = System.Math.Max(columns, line.Length);
        var pixel = Mathf.Min(button.Size.x * 0.9f / (columns * 4), button.Size.y * 0.8f / (lines.Length * 6));
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        for (var character = 0; character < lines[lineIndex].Length; character++)
        {
            var glyph = Alphabet.IndexOf(lines[lineIndex][character]);
            if (glyph < 0) glyph = Alphabet.IndexOf('?');
            for (var row = 0; row < 5; row++)
            for (var column = 0; column < 3; column++)
            {
                if (((Rows[glyph][row] - '0') & (4 >> column)) == 0) continue;
                var x = (character * 4 + column - (lines[lineIndex].Length * 4 - 1) * 0.5f) * pixel;
                var z = ((lines.Length * 6 - 1) * 0.5f - lineIndex * 6 - row - 1) * pixel;
                var start = vertices.Count;
                vertices.Add(button.Rotate(new Vector3(x, 0, z)));
                vertices.Add(button.Rotate(new Vector3(x, 0, z + pixel)));
                vertices.Add(button.Rotate(new Vector3(x + pixel, 0, z + pixel)));
                vertices.Add(button.Rotate(new Vector3(x + pixel, 0, z)));
                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
        }
        var mesh = new Mesh();
        mesh.SetVertices(new Il2CppStructArray<Vector3>(vertices.ToArray()));
        mesh.SetTriangles(new Il2CppStructArray<int>(triangles.ToArray()), 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}