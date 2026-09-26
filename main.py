from __future__ import annotations

import queue
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from processor import CopyGroup, build_copy_groups, process_groups
from scanner import (
    ExistingOutputState,
    FolderPreview,
    FolderScan,
    ScanSummary,
    sanitize_output_prefix,
    scan_and_preview,
)


class StashApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("Stash")
        self.geometry("1020x770")
        self.minsize(840, 610)

        self.root_path = tk.StringVar()
        self.threshold = tk.IntVar(value=10)
        self.batch_size = tk.IntVar(value=500)
        self.output_mode = tk.StringVar(value="root")
        self.custom_output_prefix = tk.StringVar(value="Miscellaneous")
        self.delete_sources = tk.BooleanVar(value=False)
        self.status_text = tk.StringVar(value="Ready")
        self.summary_text = tk.StringVar(value="No scan run yet")

        self._messages: queue.Queue[tuple[str, object]] = queue.Queue()
        self._stop_event = threading.Event()
        self._scan_thread: threading.Thread | None = None
        self._process_thread: threading.Thread | None = None
        self._preview_plan: list[tuple[FolderScan, FolderPreview]] = []
        self._active_root: Path | None = None

        self._build_ui()
        self.after(100, self._drain_messages)

    def _build_ui(self) -> None:
        self.columnconfigure(0, weight=1)
        self.rowconfigure(3, weight=1)

        header = ttk.Frame(self, padding=(14, 12, 14, 6))
        header.grid(row=0, column=0, sticky="ew")
        header.columnconfigure(0, weight=1)

        ttk.Label(header, text="Stash", font=("Segoe UI", 18, "bold")).grid(
            row=0, column=0, sticky="w"
        )
        ttk.Label(
            header,
            text="Scan, review the plan, confirm, then copy matching images",
        ).grid(row=1, column=0, sticky="w", pady=(2, 0))

        controls = ttk.LabelFrame(self, text="Stash settings", padding=12)
        controls.grid(row=1, column=0, sticky="ew", padx=14, pady=(4, 8))
        controls.columnconfigure(1, weight=1)

        ttk.Label(controls, text="Root folder:").grid(
            row=0, column=0, sticky="w", padx=(0, 8)
        )
        self.root_entry = ttk.Entry(controls, textvariable=self.root_path)
        self.root_entry.grid(row=0, column=1, sticky="ew")
        ttk.Button(controls, text="Browse…", command=self._browse).grid(
            row=0, column=2, padx=(8, 0)
        )

        ttk.Label(controls, text="Small-folder threshold:").grid(
            row=1, column=0, sticky="w", pady=(10, 0), padx=(0, 8)
        )
        ttk.Spinbox(
            controls,
            from_=1,
            to=999,
            textvariable=self.threshold,
            width=7,
        ).grid(row=1, column=1, sticky="w", pady=(10, 0))
        ttk.Label(controls, text="images or fewer").grid(
            row=1, column=1, sticky="w", padx=(62, 0), pady=(10, 0)
        )

        ttk.Label(controls, text="Output target:").grid(
            row=2, column=0, sticky="w", pady=(8, 0), padx=(0, 8)
        )
        ttk.Spinbox(
            controls,
            from_=1,
            to=100000,
            textvariable=self.batch_size,
            width=7,
        ).grid(row=2, column=1, sticky="w", pady=(8, 0))
        ttk.Label(controls, text="images (source folders stay together)").grid(
            row=2, column=1, sticky="w", padx=(62, 0), pady=(8, 0)
        )

        ttk.Label(controls, text="Output folder name:").grid(
            row=3, column=0, sticky="nw", pady=(10, 0), padx=(0, 8)
        )

        naming = ttk.Frame(controls)
        naming.grid(row=3, column=1, columnspan=2, sticky="ew", pady=(8, 0))
        naming.columnconfigure(1, weight=1)

        ttk.Radiobutton(
            naming,
            text="Root folder name + _Misc",
            variable=self.output_mode,
            value="root",
            command=self._update_output_controls,
        ).grid(row=0, column=0, columnspan=2, sticky="w")

        ttk.Radiobutton(
            naming,
            text="Custom:",
            variable=self.output_mode,
            value="custom",
            command=self._update_output_controls,
        ).grid(row=1, column=0, sticky="w", pady=(5, 0))

        self.custom_output_entry = ttk.Entry(
            naming,
            textvariable=self.custom_output_prefix,
        )
        self.custom_output_entry.grid(
            row=1, column=1, sticky="ew", padx=(8, 0), pady=(5, 0)
        )

        self.output_example = ttk.Label(naming, text="")
        self.output_example.grid(
            row=2, column=0, columnspan=2, sticky="w", pady=(5, 0)
        )

        ttk.Checkbutton(
            controls,
            text="Delete source folders after successful copy (safe folders only)",
            variable=self.delete_sources,
        ).grid(
            row=4,
            column=1,
            columnspan=2,
            sticky="w",
            pady=(10, 0),
        )

        buttons = ttk.Frame(self, padding=(14, 0, 14, 8))
        buttons.grid(row=2, column=0, sticky="ew")
        buttons.columnconfigure(5, weight=1)

        self.scan_button = ttk.Button(
            buttons,
            text="Scan",
            command=self._start_scan,
        )
        self.scan_button.grid(row=0, column=0)

        self.stop_button = ttk.Button(
            buttons,
            text="Stop",
            command=self._stop_scan,
            state="disabled",
        )
        self.stop_button.grid(row=0, column=1, padx=(8, 0))

        ttk.Button(buttons, text="Clear Log", command=self._clear_log).grid(
            row=0, column=2, padx=(18, 0)
        )
        ttk.Button(buttons, text="Copy Log", command=self._copy_log).grid(
            row=0, column=3, padx=(8, 0)
        )

        ttk.Label(buttons, textvariable=self.summary_text).grid(
            row=0, column=5, sticky="e"
        )

        log_frame = ttk.LabelFrame(self, text="Activity log", padding=8)
        log_frame.grid(row=3, column=0, sticky="nsew", padx=14, pady=(0, 10))
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)

        self.log = ScrolledText(
            log_frame,
            wrap="none",
            font=("Consolas", 10),
            undo=False,
        )
        self.log.grid(row=0, column=0, sticky="nsew")
        self.log.configure(state="disabled")

        status = ttk.Frame(self, padding=(14, 0, 14, 10))
        status.grid(row=4, column=0, sticky="ew")
        status.columnconfigure(0, weight=1)

        ttk.Separator(status).grid(row=0, column=0, sticky="ew", pady=(0, 7))
        ttk.Label(status, textvariable=self.status_text).grid(
            row=1, column=0, sticky="w"
        )

        self.root_path.trace_add("write", self._refresh_output_example)
        self.custom_output_prefix.trace_add("write", self._refresh_output_example)
        self.output_mode.trace_add("write", self._refresh_output_example)
        self._update_output_controls()

    def _browse(self) -> None:
        selected = filedialog.askdirectory(title="Choose image root folder")
        if selected:
            self.root_path.set(selected)

    def _update_output_controls(self) -> None:
        if self.output_mode.get() == "custom":
            self.custom_output_entry.configure(state="normal")
        else:
            self.custom_output_entry.configure(state="disabled")
        self._refresh_output_example()

    def _refresh_output_example(self, *_args: object) -> None:
        raw_root = self.root_path.get().strip()
        root = Path(raw_root) if raw_root else None

        if self.output_mode.get() == "root":
            base = root.name if root and root.name else "Root"
            prefix = sanitize_output_prefix(f"{base}_Misc")
        else:
            prefix = sanitize_output_prefix(self.custom_output_prefix.get())

        self.output_example.configure(
            text=f"Example: {prefix}_001, {prefix}_002, …"
        )

    def _resolve_output_prefix(self, root: Path) -> str:
        if self.output_mode.get() == "root":
            return sanitize_output_prefix(f"{root.name}_Misc")

        raw = self.custom_output_prefix.get().strip()
        if not raw:
            raise ValueError("Enter a custom output folder name.")
        return sanitize_output_prefix(raw)

    def _read_settings(self) -> tuple[Path, int, int, str]:
        raw_root = self.root_path.get().strip()
        if not raw_root:
            raise ValueError("Choose a root folder first.")

        root = Path(raw_root)
        if not root.is_dir():
            raise ValueError("The selected root folder does not exist.")

        try:
            threshold = int(self.threshold.get())
            batch_size = int(self.batch_size.get())
        except (tk.TclError, ValueError):
            raise ValueError("Enter valid numeric settings.") from None

        if threshold < 1 or batch_size < 1:
            raise ValueError("Both numeric settings must be at least 1.")

        output_prefix = self._resolve_output_prefix(root)
        return root.resolve(), threshold, batch_size, output_prefix

    def _start_scan(self) -> None:
        if self._scan_thread and self._scan_thread.is_alive():
            return
        if self._process_thread and self._process_thread.is_alive():
            return

        try:
            root, threshold, batch_size, output_prefix = self._read_settings()
        except ValueError as exc:
            messagebox.showerror("Stash", str(exc))
            return

        self._clear_log()
        self._stop_event.clear()
        self._preview_plan.clear()
        self._active_root = root

        self.scan_button.configure(state="disabled")
        self.stop_button.configure(state="normal")
        self.status_text.set("Scanning…")
        self.summary_text.set("Scanning…")

        self._append_log("STASH — SCAN")
        self._append_log(f"Root: {root}")
        self._append_log(f"Match rule: folders containing 1 to {threshold} images")
        self._append_log(f"Output target: {batch_size} images")
        self._append_log(f"Output folder prefix: {output_prefix}")
        self._append_log(
            "Rule: an original source folder is never split between output folders."
        )
        self._append_log(
            f"Existing {output_prefix}_NNN folders are excluded from source scanning."
        )
        self._append_log(
            "Delete source folders: "
            + ("YES (safe folders only)" if self.delete_sources.get() else "NO")
        )
        self._append_log("-" * 84)

        self._scan_thread = threading.Thread(
            target=self._scan_worker,
            args=(root, threshold, batch_size, output_prefix),
            daemon=True,
        )
        self._scan_thread.start()

    def _scan_worker(
        self,
        root: Path,
        threshold: int,
        batch_size: int,
        output_prefix: str,
    ) -> None:
        def report_existing(state: ExistingOutputState) -> None:
            self._messages.put(("existing", state))

        def report_folder(
            result: FolderScan,
            qualifies: bool,
            preview: FolderPreview | None,
        ) -> None:
            self._messages.put(("folder", (result, qualifies, preview)))

        try:
            summary = scan_and_preview(
                root=root,
                threshold=threshold,
                batch_size=batch_size,
                output_prefix=output_prefix,
                on_existing_output=report_existing,
                on_folder=report_folder,
                stop_event=self._stop_event,
            )
            self._messages.put(("done", summary))
        except Exception as exc:
            self._messages.put(("scan_error", str(exc)))

    def _stop_scan(self) -> None:
        self._stop_event.set()
        self.status_text.set("Stopping after the current folder…")
        self.stop_button.configure(state="disabled")

    def _start_processing_after_scan(self, summary: ScanSummary) -> None:
        if self._active_root is None or not self._preview_plan:
            self._finish_idle()
            return

        groups = build_copy_groups(self._active_root, self._preview_plan)
        files_to_copy = sum(len(group.items) for group in groups)
        output_folders = {
            item.destination.parent
            for group in groups
            for item in group.items
        }
        delete_requested = bool(self.delete_sources.get())
        deletable = sum(1 for group in groups if group.safe_to_delete_source)
        protected = len(groups) - deletable

        if delete_requested:
            cleanup_text = (
                f"\nSource-folder deletion is ON.\n"
                f"{deletable} source folder(s) are currently safe to delete after "
                f"verified copying."
            )
            if protected:
                cleanup_text += (
                    f"\n{protected} source folder(s) contain extra content and will "
                    "be left untouched."
                )
        else:
            cleanup_text = "\nSource-folder deletion is OFF. Originals will remain."

        confirmed = messagebox.askyesno(
            "Stash — Confirm",
            f"Scan complete.\n\n"
            f"{summary.qualifying_folders} matching folder(s)\n"
            f"{files_to_copy} image(s) will be copied\n"
            f"{len(output_folders)} output folder(s) will be used"
            f"{cleanup_text}\n\n"
            "Continue?",
        )

        if not confirmed:
            self._append_log("")
            self._append_log("Processing cancelled. No files were changed.")
            self.status_text.set("Scan complete — processing cancelled")
            self._finish_idle()
            return

        self.stop_button.configure(state="disabled")
        self.status_text.set("Copying…")
        self._append_log("")
        self._append_log("=" * 84)
        self._append_log("PROCESSING")
        self._append_log(f"Copying {files_to_copy} image(s)…")
        if delete_requested:
            self._append_log(
                "Safe source folders will be removed only after verified copying."
            )
        else:
            self._append_log("Original source files and folders will be kept.")
        self._append_log("-" * 84)

        self._process_thread = threading.Thread(
            target=self._process_worker,
            args=(groups, delete_requested),
            daemon=True,
        )
        self._process_thread.start()

    def _process_worker(
        self,
        groups: tuple[CopyGroup, ...],
        delete_requested: bool,
    ) -> None:
        def report_copy(item: object) -> None:
            self._messages.put(("copied", item))

        def report_source(
            folder: Path,
            deleted: bool,
            reason: str | None,
        ) -> None:
            self._messages.put(("source_result", (folder, deleted, reason)))

        try:
            summary = process_groups(
                groups=groups,
                delete_source_folders=delete_requested,
                on_copy=report_copy,
                on_source_result=report_source,
            )
            self._messages.put(("process_done", summary))
        except Exception as exc:
            self._messages.put(("process_error", str(exc)))

    def _drain_messages(self) -> None:
        try:
            while True:
                kind, payload = self._messages.get_nowait()

                if kind == "existing":
                    self._show_existing_output(payload)  # type: ignore[arg-type]
                elif kind == "folder":
                    result, qualifies, preview = payload  # type: ignore[misc]
                    self._show_folder(result, qualifies, preview)
                elif kind == "done":
                    self._show_scan_summary(payload)  # type: ignore[arg-type]
                elif kind == "scan_error":
                    self._show_scan_error(str(payload))
                elif kind == "copied":
                    self._show_copied(payload)
                elif kind == "source_result":
                    folder, deleted, reason = payload  # type: ignore[misc]
                    self._show_source_result(folder, deleted, reason)
                elif kind == "process_done":
                    self._show_process_done(payload)
                elif kind == "process_error":
                    self._show_process_error(str(payload))
        except queue.Empty:
            pass
        finally:
            self.after(100, self._drain_messages)

    def _show_existing_output(self, state: ExistingOutputState) -> None:
        if state.folder_count == 0:
            self._append_log(
                f"Existing {state.output_prefix} output: none found"
            )
            self._append_log("Next global sequence: 000001")
        else:
            latest_name = f"{state.output_prefix}_{state.latest_index:03d}"
            self._append_log(
                f"Existing {state.output_prefix} output: {state.folder_count} folder(s), "
                f"{state.image_count} image(s)"
            )
            self._append_log(
                f"Latest output: {latest_name} — {state.latest_image_count} image(s)"
            )
            self._append_log(
                f"Highest global sequence found: {state.highest_sequence:06d}"
            )
            self._append_log(
                f"Next global sequence: {state.highest_sequence + 1:06d}"
            )

        self._append_log("-" * 84)

    def _show_folder(
        self,
        result: FolderScan,
        qualifies: bool,
        preview: FolderPreview | None,
    ) -> None:
        count_word = "image" if result.image_count == 1 else "images"

        if not qualifies or preview is None:
            self._append_log(
                f"[KEEP ] {result.relative_path} — {result.image_count} {count_word}"
            )
            return

        self._preview_plan.append((result, preview))

        self._append_log(
            f"[MATCH] {result.relative_path} — {result.image_count} {count_word} "
            f"-> {preview.destination_folder}"
        )

        for item in preview.images:
            self._append_log(
                f"        {item.source_sequence:>3}. {item.source_name}"
            )
            self._append_log(
                f"             -> {item.destination_folder}\\{item.destination_name}"
            )

    def _show_scan_summary(self, summary: ScanSummary) -> None:
        self._append_log("-" * 84)
        self._append_log(f"Folders scanned: {summary.folders_scanned}")
        self._append_log(f"Images found: {summary.images_found}")
        self._append_log(f"Matching folders: {summary.qualifying_folders}")
        self._append_log(f"Images in matching folders: {summary.qualifying_images}")

        if summary.planned_destination_folders:
            self._append_log(
                "Planned destinations: "
                + ", ".join(summary.planned_destination_folders)
            )
        else:
            self._append_log("Planned destinations: none")

        if summary.stopped:
            self._append_log("Scan stopped by user. No files were changed.")
            self.status_text.set("Scan stopped")
            self._finish_idle()
            return

        self.summary_text.set(
            f"{summary.qualifying_folders} matches · "
            f"{summary.qualifying_images} images"
        )

        if summary.qualifying_images == 0:
            self._append_log("Scan complete. Nothing to copy.")
            self.status_text.set("Scan complete — no matches")
            self._finish_idle()
            return

        self.status_text.set("Scan complete — awaiting confirmation")
        self._start_processing_after_scan(summary)

    def _show_scan_error(self, message: str) -> None:
        self._append_log("")
        self._append_log(f"SCAN ERROR: {message}")
        self.status_text.set("Scan failed")
        self._finish_idle()
        messagebox.showerror("Stash", message)

    def _show_copied(self, item: object) -> None:
        source = getattr(item, "source", "")
        destination = getattr(item, "destination", "")
        self._append_log(f"[COPIED] {source}")
        self._append_log(f"         -> {destination}")

    def _show_source_result(
        self,
        folder: Path,
        deleted: bool,
        reason: str | None,
    ) -> None:
        if deleted:
            self._append_log(f"[DELETED SOURCE FOLDER] {folder}")
        else:
            self._append_log(
                f"[KEPT SOURCE FOLDER] {folder} — {reason or 'not safe to delete'}"
            )

    def _show_process_done(self, summary: object) -> None:
        files_copied = getattr(summary, "files_copied", 0)
        output_folders_created = getattr(summary, "output_folders_created", 0)
        source_folders_deleted = getattr(summary, "source_folders_deleted", 0)
        source_folders_kept = getattr(summary, "source_folders_kept", 0)

        self._append_log("-" * 84)
        self._append_log(f"Complete: {files_copied} image(s) copied.")
        self._append_log(
            f"Output folders created this run: {output_folders_created}"
        )

        if self.delete_sources.get():
            self._append_log(
                f"Source folders deleted: {source_folders_deleted}"
            )
            if source_folders_kept:
                self._append_log(
                    f"Source folders kept for safety: {source_folders_kept}"
                )
        else:
            self._append_log("Original source files and folders were kept.")

        self.status_text.set("Complete")
        self.summary_text.set(f"{files_copied} images copied")
        self._finish_idle()

    def _show_process_error(self, message: str) -> None:
        self._append_log("")
        self._append_log(f"PROCESSING ERROR: {message}")
        self._append_log(
            "Existing destination files were never overwritten. Source folders are "
            "only removed after all of their planned copies have verified successfully."
        )
        self.status_text.set("Processing stopped with an error")
        self._finish_idle()
        messagebox.showerror("Stash", message)

    def _finish_idle(self) -> None:
        self.scan_button.configure(state="normal")
        self.stop_button.configure(state="disabled")

    def _clear_log(self) -> None:
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        self.log.configure(state="disabled")

    def _copy_log(self) -> None:
        content = self.log.get("1.0", "end-1c")
        if not content:
            return

        self.clipboard_clear()
        self.clipboard_append(content)
        self.status_text.set("Log copied to clipboard")

    def _append_log(self, text: str) -> None:
        self.log.configure(state="normal")
        self.log.insert("end", text + "\n")
        self.log.see("end")
        self.log.configure(state="disabled")


if __name__ == "__main__":
    StashApp().mainloop()
