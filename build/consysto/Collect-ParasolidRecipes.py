"""Copy recipe and licence sources from a previously SHA-256-verified conda archive."""
import io
from pathlib import Path, PurePosixPath
import sys
import tarfile
import zipfile
from compression import zstd

archive, destination = Path(sys.argv[1]), Path(sys.argv[2]).resolve()
with zipfile.ZipFile(archive) as container:
    for name in container.namelist():
        if not name.endswith('.tar.zst'):
            continue
        with tarfile.open(fileobj=io.BytesIO(zstd.decompress(container.read(name)))) as source:
            for entry in source:
                if not entry.isfile() or not entry.name.startswith(('info/recipe/', 'info/licenses/')):
                    continue
                relative = PurePosixPath(entry.name)
                if relative.is_absolute() or '..' in relative.parts:
                    raise ValueError('Invalid source archive entry')
                target = (destination / Path(*relative.parts)).resolve()
                if not target.is_relative_to(destination):
                    raise ValueError('Source entry escapes its destination')
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.extractfile(entry) as stream:
                    target.write_bytes(stream.read())
