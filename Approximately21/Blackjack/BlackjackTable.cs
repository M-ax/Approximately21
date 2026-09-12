using System;
using UnityEngine;

namespace Approximately21.Blackjack;

public sealed class BlackjackTable : InjectableBlock
{
    private const float InteractionSurfaceHeight = 0.36f;
    private const float InteractionSurfaceWidth = 1.04f;
    private const float InteractionSurfaceDepth = 0.58f;
    private const float InteractionSurfaceCenterZ = 0.26f;

    private static readonly InjectableBlockData Data = new(
        "Approximately21_BlackjackTable",
        "Frame Quarter", "Blackjack Table", "A blackjack table.",
        CreateCheckerboardTexture,
        "Approximately21.BlackjackTableMesh.bin",
        0.1f,
        0.06806712f,
        0.2529426f,
        0.045026492f);
    private readonly BlackjackInteraction _interaction = new(Data.PrefabName);

    public BlackjackTable(IntPtr pointer)
        : base(pointer, Data)
    {
    }

    protected override bool TryConfigureAttachedComponents(EPC_SpaceshipComponent block, Core core)
    {
        _interaction.RegisterSeats(GetInteractionBounds());
        return _interaction.TryConfigureAttachedComponents(block, core, GetInteractionBounds());
    }

    protected override void UpdateAttachedComponents()
    {
        _interaction.RefreshRuntime();
        var tableMesh = GetPlacedMesh(0);
        if (tableMesh != null)
            _interaction.Update(tableMesh, GetInteractionBounds());
    }

    protected override void ResetAttachedComponents() => _interaction.ResetRendering();

    protected override void OnDestroy()
    {
        _interaction.Dispose();
        base.OnDestroy();
    }

    private Bounds GetInteractionBounds()
    {
        var tabletopMesh = GetPlacedMesh(1) ?? GetPlacedMesh(0);
        return tabletopMesh != null
            ? tabletopMesh.bounds
            : new Bounds(
                new Vector3(0f, InteractionSurfaceHeight, InteractionSurfaceCenterZ),
                new Vector3(InteractionSurfaceWidth, 0f, InteractionSurfaceDepth));
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
