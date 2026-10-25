using UnityEditor;

namespace ChibiClash.EditorTools
{
    /// <summary>Imports anything dropped into Resources/Weapons as a crisp, transparent, uncompressed sprite.</summary>
    public class WeaponTexturePostprocessor : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!assetPath.Contains("ChibiClash/Resources/Weapons/")) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.spritePixelsPerUnit = 100f;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = 1024;
        }
    }
}
