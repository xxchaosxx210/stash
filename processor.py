from __future__ import annotations

import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable

from scanner import FolderPreview, FolderScan


@dataclass(frozen=True)
class CopyItem:
    source: Path
    destination: Path


@dataclass(frozen=True)
class CopySummary:
    files_copied: int
    folders_created: int


def build_copy_items(
    root: Path,
    planned_folders: Iterable[tuple[FolderScan, FolderPreview]],
) -> tuple[CopyItem, ...]:
    items: list[CopyItem] = []

    for folder_scan, preview in planned_folders:
        for planned in preview.images:
            items.append(
                CopyItem(
                    source=folder_scan.path / planned.source_name,
                    destination=root
                    / planned.destination_folder
                    / planned.destination_name,
                )
            )

    return tuple(items)


def preflight_copy(items: Iterable[CopyItem]) -> tuple[CopyItem, ...]:
    """Validate a copy plan before creating folders or copying files."""
    checked = tuple(items)
    seen_destinations: set[Path] = set()

    for item in checked:
        if not item.source.is_file():
            raise FileNotFoundError(f"Source file is missing: {item.source}")

        normalized_destination = item.destination.resolve(strict=False)
        if normalized_destination in seen_destinations:
            raise FileExistsError(
                f"Duplicate destination in copy plan: {item.destination}"
            )
        seen_destinations.add(normalized_destination)

        if item.destination.exists():
            raise FileExistsError(
                f"Destination already exists; nothing was overwritten: "
                f"{item.destination}"
            )

    return checked


def copy_plan(
    items: Iterable[CopyItem],
    on_copy: Callable[[CopyItem], None] | None = None,
) -> CopySummary:
    """Copy a preflighted plan. Source files are never moved or deleted."""
    checked = preflight_copy(items)

    created_folders: set[Path] = set()
    files_copied = 0

    for item in checked:
        destination_folder = item.destination.parent

        if not destination_folder.exists():
            destination_folder.mkdir(parents=True, exist_ok=True)
            created_folders.add(destination_folder)

        # copy2 preserves useful file metadata such as modified time.
        shutil.copy2(item.source, item.destination)
        files_copied += 1

        if on_copy:
            on_copy(item)

    return CopySummary(
        files_copied=files_copied,
        folders_created=len(created_folders),
    )
