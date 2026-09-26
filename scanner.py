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

GLOBAL_SEQUENCE_RE = re.compile(r"^(\d+)__")
INVALID_FILENAME_RE = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
WINDOWS_RESERVED_NAMES = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
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
class ExistingOutputState:
    folder_count: int
    image_count: int
    latest_index: int
    latest_image_count: int
    highest_sequence: int
    output_prefix: str


@dataclass(frozen=True)
class PlannedImage:
    source_name: str
    destination_folder: str
    destination_name: str
    global_sequence: int
    source_sequence: int


@dataclass(frozen=True)
class FolderPreview:
    destination_folder: str
    images: tuple[PlannedImage, ...]


@dataclass(frozen=True)
class ScanSummary:
    folders_scanned: int
    images_found: int
    qualifying_folders: int
    qualifying_images: int
    planned_destination_folders: tuple[str, ...]
    stopped: bool


def natural_key(value: str) -> list[object]:
    """Return a key that sorts embedded numbers numerically."""
    return [
        int(part) if part.isdigit() else part.casefold()
        for part in re.split(r"(\d+)", value)
    ]


def sanitize_name(name: str, fallback: str) -> str:
    """Return a Windows-safe readable name while preserving valid underscores."""
    cleaned = INVALID_FILENAME_RE.sub("_", name.strip())
    cleaned = re.sub(r"\s+", "_", cleaned)
    cleaned = re.sub(r"_+", "_", cleaned)
    cleaned = cleaned.strip(" .")

    if not cleaned:
        cleaned = fallback

    if cleaned.upper() in WINDOWS_RESERVED_NAMES:
        cleaned += "_"

    return cleaned[:120].rstrip(" .") or fallback


def sanitize_folder_name(name: str) -> str:
    return sanitize_name(name, "Folder")


def sanitize_output_prefix(name: str) -> str:
    return sanitize_name(name, "Miscellaneous")


def output_folder_index(name: str, output_prefix: str) -> int | None:
    pattern = re.compile(
        rf"^{re.escape(output_prefix)}_(\d+)$",
        re.IGNORECASE,
    )
    match = pattern.fullmatch(name)
    return int(match.group(1)) if match else None


def output_folder_name(output_prefix: str, index: int) -> str:
    return f"{output_prefix}_{index:03d}"


def inspect_existing_output(root: Path, output_prefix: str) -> ExistingOutputState:
    """Read existing output folders for the selected prefix without changing them."""
    output_folders: list[tuple[int, Path]] = []

    for child in root.iterdir():
        if not child.is_dir():
            continue
        index = output_folder_index(child.name, output_prefix)
        if index is not None:
            output_folders.append((index, child))

    output_folders.sort(key=lambda item: item[0])

    total_images = 0
    highest_sequence = 0
    latest_index = 0
    latest_image_count = 0

    for index, folder in output_folders:
        image_count = 0
        try:
            entries = list(folder.iterdir())
        except OSError:
            entries = []

        for item in entries:
            if not item.is_file():
                continue

            if item.suffix.casefold() in IMAGE_EXTENSIONS:
                image_count += 1
                total_images += 1

            match = GLOBAL_SEQUENCE_RE.match(item.name)
            if match:
                highest_sequence = max(highest_sequence, int(match.group(1)))

        if index >= latest_index:
            latest_index = index
            latest_image_count = image_count

    return ExistingOutputState(
        folder_count=len(output_folders),
        image_count=total_images,
        latest_index=latest_index,
        latest_image_count=latest_image_count,
        highest_sequence=highest_sequence,
        output_prefix=output_prefix,
    )


class PreviewPlanner:
    """Calculate future destinations and names without touching the filesystem."""

    def __init__(
        self,
        existing: ExistingOutputState,
        batch_size: int,
        output_prefix: str,
    ) -> None:
        if batch_size < 1:
            raise ValueError("Output target size must be at least 1.")

        self.batch_size = batch_size
        self.output_prefix = output_prefix
        self.current_index = existing.latest_index or 1
        self.current_count = existing.latest_image_count if existing.latest_index else 0
        self.next_sequence = existing.highest_sequence + 1
        self.destination_folders: list[str] = []

    def plan_folder(self, result: FolderScan) -> FolderPreview:
        # Keep each original source folder together. Once the current output
        # folder has reached the target, the next source folder starts a new one.
        if self.current_count >= self.batch_size:
            self.current_index += 1
            self.current_count = 0

        destination_folder = output_folder_name(
            self.output_prefix,
            self.current_index,
        )
        if destination_folder not in self.destination_folders:
            self.destination_folders.append(destination_folder)

        source_label = sanitize_folder_name(result.path.name)
        planned: list[PlannedImage] = []

        for source_sequence, source_name in enumerate(result.image_names, start=1):
            extension = Path(source_name).suffix
            destination_name = (
                f"{self.next_sequence:06d}__"
                f"{source_label}__"
                f"{source_sequence:03d}"
                f"{extension}"
            )

            planned.append(
                PlannedImage(
                    source_name=source_name,
                    destination_folder=destination_folder,
                    destination_name=destination_name,
                    global_sequence=self.next_sequence,
                    source_sequence=source_sequence,
                )
            )
            self.next_sequence += 1

        self.current_count += result.image_count
        return FolderPreview(
            destination_folder=destination_folder,
            images=tuple(planned),
        )


def scan_and_preview(
    root: Path,
    threshold: int,
    batch_size: int,
    output_prefix: str,
    on_existing_output: Callable[[ExistingOutputState], None] | None = None,
    on_folder: Callable[[FolderScan, bool, FolderPreview | None], None] | None = None,
    stop_event: Event | None = None,
) -> ScanSummary:
    """Read the tree and calculate a future consolidation plan.

    This function is deliberately read-only. It never creates, moves, copies,
    renames, deletes, or modifies files or directories.
    """
    root = root.expanduser().resolve()
    if not root.is_dir():
        raise NotADirectoryError(f"Root folder does not exist: {root}")
    if threshold < 1:
        raise ValueError("Threshold must be at least 1.")
    if batch_size < 1:
        raise ValueError("Output target size must be at least 1.")

    output_prefix = sanitize_output_prefix(output_prefix)
    existing = inspect_existing_output(root, output_prefix)

    if on_existing_output:
        on_existing_output(existing)

    planner = PreviewPlanner(existing, batch_size, output_prefix)

    folders_scanned = 0
    images_found = 0
    qualifying_folders = 0
    qualifying_images = 0
    stopped = False

    for current_dir, dir_names, file_names in os.walk(
        root, topdown=True, followlinks=False
    ):
        if stop_event and stop_event.is_set():
            stopped = True
            break

        dir_names.sort(key=natural_key)
        file_names.sort(key=natural_key)

        current_path = Path(current_dir)

        if current_path == root:
            # Never scan the currently selected Stash output folders as sources.
            dir_names[:] = [
                name
                for name in dir_names
                if output_folder_index(name, output_prefix) is None
            ]
            # Preserve compatibility with the original default naming scheme.
            if output_prefix.casefold() != "miscellaneous":
                dir_names[:] = [
                    name
                    for name in dir_names
                    if output_folder_index(name, "Miscellaneous") is None
                ]
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
        preview = None

        if qualifies:
            qualifying_folders += 1
            qualifying_images += result.image_count
            preview = planner.plan_folder(result)

        if on_folder:
            on_folder(result, qualifies, preview)

    return ScanSummary(
        folders_scanned=folders_scanned,
        images_found=images_found,
        qualifying_folders=qualifying_folders,
        qualifying_images=qualifying_images,
        planned_destination_folders=tuple(planner.destination_folders),
        stopped=stopped,
    )
