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
class CopyGroup:
    source_folder: Path
    items: tuple[CopyItem, ...]
    safe_to_delete_source: bool
    delete_block_reason: str | None


@dataclass(frozen=True)
class ProcessSummary:
    files_copied: int
    output_folders_created: int
    source_folders_deleted: int
    source_folders_kept: int


def _source_folder_delete_check(
    folder_scan: FolderScan,
    items: tuple[CopyItem, ...],
) -> tuple[bool, str | None]:
    """Only allow deletion when the source folder contains exactly planned files."""
    try:
        entries = tuple(folder_scan.path.iterdir())
    except OSError as exc:
        return False, f"could not inspect source folder: {exc}"

    if any(entry.is_dir() for entry in entries):
        return False, "contains one or more subfolders"

    planned_sources = {item.source.resolve(strict=False) for item in items}
    actual_files = {
        entry.resolve(strict=False)
        for entry in entries
        if entry.is_file()
    }

    extras = actual_files - planned_sources
    missing = planned_sources - actual_files

    if missing:
        return False, "one or more planned source files are missing"

    if extras:
        return False, "contains files that are not part of the copy plan"

    return True, None


def build_copy_groups(
    root: Path,
    planned_folders: Iterable[tuple[FolderScan, FolderPreview]],
) -> tuple[CopyGroup, ...]:
    groups: list[CopyGroup] = []

    for folder_scan, preview in planned_folders:
        items = tuple(
            CopyItem(
                source=folder_scan.path / planned.source_name,
                destination=root
                / planned.destination_folder
                / planned.destination_name,
            )
            for planned in preview.images
        )

        safe_to_delete, reason = _source_folder_delete_check(folder_scan, items)
        groups.append(
            CopyGroup(
                source_folder=folder_scan.path,
                items=items,
                safe_to_delete_source=safe_to_delete,
                delete_block_reason=reason,
            )
        )

    return tuple(groups)


def preflight_groups(groups: Iterable[CopyGroup]) -> tuple[CopyGroup, ...]:
    """Validate the complete plan before creating folders or copying files."""
    checked = tuple(groups)
    seen_destinations: set[Path] = set()

    for group in checked:
        for item in group.items:
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
                    "Destination already exists; nothing was overwritten: "
                    f"{item.destination}"
                )

    return checked


def _verified_copy(source: Path, destination: Path) -> None:
    shutil.copy2(source, destination)

    if not destination.is_file():
        raise OSError(f"Copied file was not found at destination: {destination}")

    source_size = source.stat().st_size
    destination_size = destination.stat().st_size
    if source_size != destination_size:
        raise OSError(
            "Copy verification failed because file sizes differ: "
            f"{source} -> {destination}"
        )


def process_groups(
    groups: Iterable[CopyGroup],
    delete_source_folders: bool,
    on_copy: Callable[[CopyItem], None] | None = None,
    on_source_result: Callable[[Path, bool, str | None], None] | None = None,
) -> ProcessSummary:
    """Copy planned images and optionally remove verified, safe source folders.

    All destinations are checked before any writes. A source folder is only
    deleted after every planned file in that folder has copied and verified.
    """
    checked = preflight_groups(groups)

    created_output_folders: set[Path] = set()
    files_copied = 0
    source_folders_deleted = 0
    source_folders_kept = 0

    for group in checked:
        for item in group.items:
            destination_folder = item.destination.parent
            if not destination_folder.exists():
                destination_folder.mkdir(parents=True, exist_ok=True)
                created_output_folders.add(destination_folder)

            _verified_copy(item.source, item.destination)
            files_copied += 1

            if on_copy:
                on_copy(item)

        if not delete_source_folders:
            continue

        if not group.safe_to_delete_source:
            source_folders_kept += 1
            if on_source_result:
                on_source_result(
                    group.source_folder,
                    False,
                    group.delete_block_reason or "source folder was not safe to delete",
                )
            continue

        # Re-check immediately before deletion in case the folder changed while
        # processing. If anything unexpected appears, leave the source untouched.
        current_files = {
            entry.resolve(strict=False)
            for entry in group.source_folder.iterdir()
            if entry.is_file()
        }
        planned_sources = {
            item.source.resolve(strict=False)
            for item in group.items
        }
        has_subfolders = any(
            entry.is_dir() for entry in group.source_folder.iterdir()
        )

        if has_subfolders or current_files != planned_sources:
            source_folders_kept += 1
            if on_source_result:
                on_source_result(
                    group.source_folder,
                    False,
                    "folder contents changed or extra content was found",
                )
            continue

        # Every destination in this group has already been copied and size-verified.
        for item in group.items:
            item.source.unlink()

        group.source_folder.rmdir()
        source_folders_deleted += 1

        if on_source_result:
            on_source_result(group.source_folder, True, None)

    return ProcessSummary(
        files_copied=files_copied,
        output_folders_created=len(created_output_folders),
        source_folders_deleted=source_folders_deleted,
        source_folders_kept=source_folders_kept,
    )
