from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path
from threading import Event
from typing import Callable

IMAGE_EXTENSIONS = {
    ".jpg",
    ".jpeg",
    ".png",
    ".webp",
    ".gif",
    ".bmp",
    ".tif",
    ".tiff",
}


@dataclass(frozen=True)
class FolderScan:
    path: Path
    relative_path: Path
    image_names: tuple[str, ...]
    subfolder_count: int

    @property
    def image_count(self) -> int:
        return len(self.image_names)


@dataclass(frozen=True)
class ScanSummary:
    folders_scanned: int
    images_found: int
    qualifying_folders: int
    qualifying_images: int
    stopped: bool


def natural_key(value: str) -> list[object]:
    """Return a key that sorts embedded numbers numerically."""
    return [int(part) if part.isdigit() else part.casefold() for part in re.split(r"(\d+)", value)]


def scan_root(
    root: Path,
    threshold: int,
    on_folder: Callable[[FolderScan, bool], None] | None = None,
    stop_event: Event | None = None,
) -> ScanSummary:
    """Read a directory tree and report direct image counts for each subfolder.

    This function is deliberately read-only. It never creates, moves, renames,
    deletes, or modifies files or directories.
    """
    root = root.expanduser().resolve()
    if not root.is_dir():
        raise NotADirectoryError(f"Root folder does not exist: {root}")
    if threshold < 1:
        raise ValueError("Threshold must be at least 1.")

    folders_scanned = 0
    images_found = 0
    qualifying_folders = 0
    qualifying_images = 0
    stopped = False

    for current_dir, dir_names, file_names in os.walk(root, topdown=True, followlinks=False):
        if stop_event and stop_event.is_set():
            stopped = True
            break

        dir_names.sort(key=natural_key)
        file_names.sort(key=natural_key)

        current_path = Path(current_dir)
        if current_path == root:
            # The root itself is the container; only its subfolders are candidates.
            continue

        image_names = tuple(
            name
            for name in file_names
            if Path(name).suffix.casefold() in IMAGE_EXTENSIONS
        )

        result = FolderScan(
            path=current_path,
            relative_path=current_path.relative_to(root),
            image_names=image_names,
            subfolder_count=len(dir_names),
        )

        folders_scanned += 1
        images_found += result.image_count

        qualifies = 1 <= result.image_count <= threshold
        if qualifies:
            qualifying_folders += 1
            qualifying_images += result.image_count

        if on_folder:
            on_folder(result, qualifies)

    return ScanSummary(
        folders_scanned=folders_scanned,
        images_found=images_found,
        qualifying_folders=qualifying_folders,
        qualifying_images=qualifying_images,
        stopped=stopped,
    )
