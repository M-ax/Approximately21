using System;
using UnityEngine;

namespace Approximately21;

public sealed class BlackjackTable : InjectableBlock
{
    private static readonly InjectableBlockData Data = new(
        "Approximately21_CheckerboardBlock",
        "Frame Quarter",
        CreateCheckerboardTexture,
        "Approximately21_CheckerboardBlock.bundle",
        "BlackjackTableMesh",
        "Approximately21.BlackjackTableMesh.bin",
        0.1f,
        0.06806712f,
        0.2529426f,
        0.045026492f);

    public BlackjackTable(IntPtr pointer)
        : base(pointer)
    {
    }

    internal static void RegisterDefinition()
    {
        InjectableBlockConfiguration.Register(
            typeof(BlackjackTable),
            Data,
            "Checkerboard Block",
            "A pink-and-black checkerboard test block.");
    }

    private static Texture2D CreateCheckerboardTexture()
    {
        var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false)
        {
            name = Data.PrefabName + "_Icon",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };
        var pixels = new Color32[64];
        var pink = new Color32(255, 0, 144, 255);
        var black = new Color32(0, 0, 0, 255);

        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            pixels[y * 8 + x] = (x + y) % 2 == 0 ? pink : black;

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

}