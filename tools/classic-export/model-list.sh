#!/bin/bash
# Writes the list of models to export, derived from the original data:
# every MACHINE and CONSTRUCTION item of the research tree, plus every level of the plain buildings.
#   model-list.sh <data/original folder>  > models.txt
set -e
DATA="${1:?path to data/original}"

awk '
/^(MACHINE|CONSTRUCTION)/ { kind=$1; type=$2; subtype=$3; level=""; combo="" }
/^HWLEVEL/                { level=$2 }
/^WCOMBO/                 { combo=$2 }
/^END/                    { print kind, type, level, subtype, combo }
' "$DATA/global.rst" | sed 's/  */ /g; s/ $//'

# Buildings that are not research items, at every hardware level (machphys/levels.cpp).
cat <<'EOF'
CONSTRUCTION HARDWARE_LAB 1 MILITARY
CONSTRUCTION HARDWARE_LAB 3 MILITARY
CONSTRUCTION HARDWARE_LAB 1 CIVILIAN
CONSTRUCTION HARDWARE_LAB 3 CIVILIAN
CONSTRUCTION SMELTER 1
CONSTRUCTION SMELTER 3
CONSTRUCTION FACTORY 1 MILITARY
CONSTRUCTION FACTORY 3 MILITARY
CONSTRUCTION FACTORY 4 MILITARY
CONSTRUCTION FACTORY 5 MILITARY
CONSTRUCTION FACTORY 1 CIVILIAN
CONSTRUCTION FACTORY 3 CIVILIAN
CONSTRUCTION FACTORY 5 CIVILIAN
CONSTRUCTION GARRISON 1
OTHER ORE_HOLOGRAPH 1
CONSTRUCTION MINE 1
CONSTRUCTION MINE 3
CONSTRUCTION MINE 5
CONSTRUCTION BEACON 1
CONSTRUCTION BEACON 3
EOF
