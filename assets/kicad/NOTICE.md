### KiCad Symbol Libraries — bundled data

This application ships `kicad-symbols-master.zip`, an unmodified copy of the official
**KiCad symbol libraries** (https://gitlab.com/kicad/libraries/kicad-symbols).

* Upstream licence: **Creative Commons Attribution-ShareAlike 4.0 International**
  (CC-BY-SA 4.0), with the KiCad library design exception.
* Full licence text and exception: `LICENSE-kicad.md` in this folder, also available at
  https://kicad.org/libraries/license/.
* Copyright: the individual symbol authors and the KiCad libraries team.

### What BreadboardAOI does with it

The builder's **KICAD SYMBOL / FOOTPRINT** picker reads `.kicad_sym` files
**directly from the zip at runtime**; no symbol data is modified, extracted
permanently, or redistributed in altered form. Symbol ids supply footprint
metadata for the 2D board; the library is not rendered as a schematic sheet.

### Design exception

> To the extent that the creation of electronic designs that use 'Licensed Material'
> can be considered to be 'Adapted Material', then the copyright holder waives article 3
> of the license with respect to these designs and any generated files which use data
> provided as part of the 'Licensed Material'.

Circuits you draw on the breadboard are your designs and are not derived works of
the library.

### Attribution

KiCad symbol libraries © KiCad contributors, licensed CC-BY-SA 4.0.
Bundled unmodified; see `LICENSE-kicad.md`.
