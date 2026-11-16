# TODO

1. Geosets are still shown after seal creation. `SetShow(1)` on the "Seal faces" geoset does not hide it.
2. The intersected "Seal" body still shows red instead of green. Probably needs its graphic properties reset (including children) before `SetRealColor(0, 255, 0, 1)` in `CatiaSeals.MakeSeal`.
