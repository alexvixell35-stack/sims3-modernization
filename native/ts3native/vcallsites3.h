// Probe pass 3 (docs/109 §8.8): the four shared-helper sites of docs/109 §8.4 that G2 still needs attributed by thread,
// rows copied from vcallsites2.h (address, length, instruction bytes checked before arming). Used only by a build with
// /DTS3N_VCSITES3; the shipped build includes vcallsites.h.
static const VcSiteDef kVcSites[] = {
    {TS3A(0x5BF1FB), 2, {0xFF, 0xD2}},  // 0x5BF1E0 (vector release loop), search caller LotGraph_dtor
    {TS3A(0x8A65EB), 2, {0xFF, 0xD2}},  // 0x8A65D0 (vector release loop), search caller 0x628EA0 from LotGraph_ctor
    {TS3A(0x8CE574), 2, {0xFF, 0xD2}},  // 0x8CE540 (refcounted range assign), search caller 0x621DB0 on the LotGraph_Build path
    {TS3A(0x9158AB), 2, {0xFF, 0xD2}},  // RefCounted_ReleaseAt8 0x915890, search caller LotGraph_dtor 0x62B9EC
};
