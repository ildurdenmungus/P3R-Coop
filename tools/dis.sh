#!/bin/sh
# Disassemble P3R.exe from VA $1 for $2 bytes (default 0x200) with MSVC dumpbin.
# usage: tools/dis.sh 0x141613bb0 [0x100]
DUMPBIN="/c/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/dumpbin.exe"
EXE='D:\SteamLibrary\steamapps\common\P3R\P3R\Binaries\Win64\P3R.exe'
start=$1; len=${2:-0x200}
end=$(printf '0x%x' $((start + len)))
MSYS_NO_PATHCONV=1 "$DUMPBIN" -nologo -disasm:nobytes -range:$start,$end "$EXE" | grep -E '^\s+0000' | sed 's/^ *0000000//'
