namespace UCanAccess.AccessCrypto;

internal enum AccessRootTransform
{
    None,
    Rc4Only,
    Rc4ThenHeaderMask,
    HeaderMaskThenRc4,
}
