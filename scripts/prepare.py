"""Stage an isolated mod profile; Steam's game files are read through symlinks."""
from pathlib import Path
import shutil
root = Path(__file__).resolve().parents[1]
game = Path.home() / '.local/share/Steam/steamapps/common/Valheim'
profile = root / 'runtime/game'
profile.mkdir(parents=True, exist_ok=True)
for entry in game.iterdir():
    dest = profile / entry.name
    if not dest.exists():
        dest.symlink_to(entry, target_is_directory=entry.is_dir())
shutil.copytree(root / '.tools/bepinex/BepInExPack_Valheim', profile, dirs_exist_ok=True)
plugins = profile / 'BepInEx/plugins'
plugins.mkdir(exist_ok=True)
shutil.copy2(root / 'plugin/bin/Release/netstandard2.1/Bjorn.dll', plugins / 'Bjorn.dll')
config = profile / 'BepInEx/config/local.bjorn.companion.cfg'
if not config.exists():
    config.write_text('[Bot]\nEnabled = true\nName = Bjorn\n\n[Bridge]\nTokenFile = ' + str(root / 'runtime/bridge.token') + '\n')
print('Prepared:', profile)
