// Intentionally empty.
//
// An earlier draft used Unity's built-in encoders (Texture2D.EncodeToPNG/JPG)
// to avoid any external dependency. We replaced it with MagickNetBootstrap,
// which delegates to a Magick.NET DLL that's auto-downloaded by
// MagickNetInstaller on first use of the Texture Optimizer. The Magick.NET
// path is faster (no AssetDatabase round-trip per texture) and produces
// visibly cleaner downscales (Lanczos vs bilinear blit).
//
// The file is kept (empty) so the .meta GUID stays valid for any
// repository that already had this script imported. Safe to delete in a
// future cleanup pass once nobody references it.
