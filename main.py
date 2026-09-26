from __future__ import annotations

import queue
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

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
        self.geometry("1000x750")
        self.minsize(820, 590)

        self.root_path = tk.StringVar()
        self.threshold = tk.IntVar(value=10)
        self.batch_size = tk.IntVar(value=500)
        self.output_mode = tk.StringVar(value="root")
        self.custom_output_prefix = tk.StringVar(value="Miscellaneous")
        self.status_text = tk.StringVar(value="Ready")
        self.summary_text = tk.StringVar(value="No scan run yet")

        self._messages: queue.Queue[tuple[str, object]] = queue.Queue()
        self._stop_event = threading.Event()
        self._scan_thread: threading.Thread | None = None

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
            text="Read-only consolidation preview — nothing is changed",
        ).grid(row=1, column=0, sticky="w", pady=(2, 0))

        controls = ttk.LabelFrame(self, text="Preview settings", padding=12)
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

        self.root_path.trace_add("write", self._refresh_output_example)
        self.custom_output_prefix.trace_add("write", self._refresh_output_example)
        self.output_mode.trace_add("write", self._refresh_output_example)
        self._update_output_controls()

        buttons = ttk.Frame(self, padding=(14, 0, 14, 8))
        buttons.grid(row=2, column=0, sticky="ew")
        buttons.columnconfigure(5, weight=1)

        self.scan_button = ttk.Button(
            buttons, text="Scan & Preview", command=self._start_scan
        )
        self.scan_button.grid(row=0, column=0)

        self.stop_button = ttk.Button(
            buttons, text="Stop", command=self._stop_scan, state="disabled"
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

        log_frame = ttk.LabelFrame(self, text="Read-only preview log", padding=8)
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

    def _start_scan(self) -> None:
        if self._scan_thread and self._scan_thread.is_alive():
            return

        raw_root = self.root_path.get().strip()
        if not raw_root:
            messagebox.showwarning("Stash", "Choose a root folder first.")
            return

        root = Path(raw_root)
        if not root.is_dir():
            messagebox.showerror("Stash", "The selected root folder does not exist.")
            return

        try:
            threshold = int(self.threshold.get())
            batch_size = int(self.batch_size.get())
            output_prefix = self._resolve_output_prefix(root)
        except (tk.TclError, ValueError) as exc:
            messagebox.showerror("Stash", str(exc) or "Enter valid settings.")
            return

        if threshold < 1 or batch_size < 1:
            messagebox.showerror("Stash", "Both numeric settings must be at least 1.")
            return

        self._clear_log()
        self._stop_event.clear()
        self.scan_button.configure(state="disabled")
        self.stop_button.configure(state="normal")
        self.status_text.set("Scanning and building preview…")
        self.summary_text.set("Scanning…")

        self._append_log("STASH — READ-ONLY CONSOLIDATION PREVIEW")
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
        self._append_log("PREVIEW ONLY — no files or folders will be changed.")
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
            self._messages.put(("error", str(exc)))

    def _stop_scan(self) -> None:
        self._stop_event.set()
        self.status_text.set("Stopping after the current folder…")
        self.stop_button.configure(state="disabled")

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
                    self._show_summary(payload)  # type: ignore[arg-type]
                elif kind == "error":
                    self._finish_scan()
                    self.status_text.set("Scan failed")
                    self._append_log("")
                    self._append_log(f"ERROR: {payload}")
                    messagebox.showerror("Stash", str(payload))
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

    def _show_summary(self, summary: ScanSummary) -> None:
        self._append_log("-" * 84)
        self._append_log(f"Folders scanned: {summary.folders_scanned}")
        self._append_log(f"Images found: {summary.images_found}")
        self._append_log(f"Matching folders: {summary.qualifying_folders}")
        self._append_log(f"Images in matching folders: {summary.qualifying_images}")

        if summary.planned_destination_folders:
            self._append_log(
                "Preview destinations: "
                + ", ".join(summary.planned_destination_folders)
            )
        else:
            self._append_log("Preview destinations: none")

        if summary.stopped:
            self._append_log("Scan stopped by user before completion.")
            self.status_text.set("Preview stopped")
        else:
            self._append_log(
                "Preview complete. No files or folders were created, moved, copied, "
                "renamed, deleted, or modified."
            )
            self.status_text.set("Preview complete")

        self.summary_text.set(
            f"{summary.qualifying_folders} matches · "
            f"{summary.qualifying_images} images"
        )
        self._finish_scan()

    def _finish_scan(self) -> None:
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
