"""
Writes Unity .meta files next to generated PNGs so the open editor imports them with the right settings the moment it
sees them (the project's AssetPostprocessor only covers Resources/Weapons).

    kind="anim"       horizontal sprite strips (Resources/Animations): no mipmaps, no power-of-two rescale,
                      max size 16384 so a 28-frame strip (10752 px) is not shrunk, clamp, high-quality compression
    kind="character"  full-body drawings / portraits (Resources/Characters): sprite, mipmaps, max size 1024

The GUID is derived from the asset path (stable across re-runs); an existing meta keeps its GUID. The meta is written
BEFORE the PNG by save_with_meta, so an auto-refresh never imports the texture with default settings.
"""
import hashlib
import os
import re

import cv2

_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: {mip}
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: {size}
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 1
  spriteExtrude: 1
  spriteMeshType: 0
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationMethod: 0
  spriteTessellationDetail: -1
  spriteGeometrySubdivision: -1
  textureType: 8
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: {size}
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 2
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""

KINDS = {"anim": dict(mip=0, size=16384), "character": dict(mip=1, size=1024)}


def _guid(path):
    m = path + ".meta"
    if os.path.exists(m):
        g = re.search(r"^guid:\s*([0-9a-f]{32})", open(m, encoding="utf-8").read(), re.M)
        if g:
            return g.group(1)
    rel = os.path.abspath(path).replace("\\", "/")
    rel = rel[rel.find("Assets/"):] if "Assets/" in rel else rel
    return hashlib.md5(("chibiclash:" + rel).encode("utf-8")).hexdigest()


def write_meta(path, kind):
    """Writes (or refreshes) path.meta for a PNG of the given kind; returns the GUID."""
    g = _guid(path)
    text = _TEMPLATE.format(guid=g, **KINDS[kind])
    with open(path + ".meta", "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return g


def write_folder_meta(folder):
    """Folder metas (Unity creates them too, but writing one keeps the GUID stable for the commit)."""
    m = folder.rstrip("/\\") + ".meta"
    if os.path.exists(m):
        return
    g = _guid(folder.rstrip("/\\"))
    with open(m, "w", encoding="utf-8", newline="\n") as f:
        f.write("fileFormatVersion: 2\nguid: %s\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
                "  userData: \n  assetBundleName: \n  assetBundleVariant: \n" % g)


def in_assets(path):
    return "/Assets/" in os.path.abspath(path).replace("\\", "/") + "/"


def save_with_meta(path, bgra, kind=None):
    """cv2 BGRA image -> PNG (+ .meta first when the file lives under Assets/ and a kind is given)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if kind and in_assets(path):
        write_meta(path, kind)
    cv2.imwrite(path, bgra, [cv2.IMWRITE_PNG_COMPRESSION, 9])
