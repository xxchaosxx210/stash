from __future__ import annotations

import queue
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from scanner import FolderScan, ScanSummary, scan_root


class StashApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("Stash")
        self.geometry("920x650")
        self.minsize(760, 520)

        self.root_path = tk.StringVar()
        self.threshold = tk.IntVar(value=10)
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
            text="Read-only scanner — no files or folders are changed",
        ).grid(row=1, column=0, sticky="w", pady=(2, 0))

        controls = ttk.LabelFrame(self, text="Scan settings", padding=12)
        controls.grid(row=1, column=0, sticky="ew", padx=14, pady=(4, 8))
        controls.columnconfigure(1, weight=1)

        ttk.Label(controls, text="Root folder:").grid(row=0, column=0, sticky="w", padx=(0, 8))
        self.root_entry = ttk.Entry(controls, textvariable=self.root_path)
        self.root_entry.grid(row=0, column=1, sticky="ew")
        ttk.Button(controls, text="Browse…", command=self._browse).grid(
            row=0, column=2, padx=(8, 0)
        )

        ttk.Label(controls, text="Small-folder threshold:").grid(
            row=1, column=0, sticky="w", pady=(10, 0), padx=(0, 8)
        )
        threshold_box = ttk.Spinbox(
            controls,
            from_=1,
            to=999,
            textvariable=self.threshold,
            width=7,
        )
        threshold_box.grid(row=1, column=1, sticky="w", pady=(10, 0))
        ttk.Label(controls, text="images or fewer").grid(
            row=1, column=1, sticky="w", padx=(62, 0), pady=(10, 0)
        )

        buttons = ttk.Frame(self, padding=(14, 0, 14, 8))
        buttons.grid(row=2, column=0, sticky="ew")
        buttons.columnconfigure(5, weight=1)

        self.scan_button = ttk.Button(buttons, text="Scan", command=self._start_scan)
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

        log_frame = ttk.LabelFrame(self, text="Scan log", padding=8)
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
        ttk.Label(status, textvariable=self.status_text).grid(row=1, column=0, sticky="w")

    def _browse(self) -> None:
        selected = filedialog.askdirectory(title="Choose image root folder")
        if selected:
            self.root_path.set(selected)

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
        except (tk.TclError, ValueError):
            messagebox.showerror("Stash", "Enter a valid image threshold.")
            return

        if threshold < 1:
            messagebox.showerror("Stash", "The image threshold must be at least 1.")
            return

        self._clear_log()
        self._stop_event.clear()
        self.scan_button.configure(state="disabled")
        self.stop_button.configure(state="normal")
        self.status_text.set("Scanning…")
        self.summary_text.set("Scanning…")

        self._append_log("STASH — READ-ONLY SCAN")
        self._append_log(f"Root: {root}")
        self._append_log(f"Match rule: folders containing 1 to {threshold} images")
        self._append_log("No files or folders will be changed.")
        self._append_log("" + "-" * 72)

        self._scan_thread = threading.Thread(
            target=self._scan_worker,
            args=(root, threshold),
            daemon=True,
        )
        self._scan_thread.start()

    def _scan_worker(self, root: Path, threshold: int) -> None:
        def report_folder(result: FolderScan, qualifies: bool) -> None:
            self._messages.put(("folder", (result, qualifies)))

        try:
            summary = scan_root(
                root=root,
                threshold=threshold,
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
                if kind == "folder":
                    result, qualifies = payload  # type: ignore[misc]
                    self._show_folder(result, qualifies)
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

    def _show_folder(self, result: FolderScan, qualifies: bool) -> None:
        marker = "MATCH" if qualifies else "KEEP "
        count_word = "image" if result.image_count == 1 else "images"
        self._append_log(
            f"[{marker}] {result.relative_path} — {result.image_count} {count_word}"
        )

        # Candidate folders show the exact natural-sorted image order that a
        # future consolidation operation would use. Large folders stay compact.
        if qualifies:
            for index, name in enumerate(result.image_names, start=1):
                self._append_log(f"        {index:>3}. {name}")

    def _show_summary(self, summary: ScanSummary) -> None:
        self._append_log("" + "-" * 72)
        self._append_log(f"Folders scanned: {summary.folders_scanned}")
        self._append_log(f"Images found: {summary.images_found}")
        self._append_log(f"Matching folders: {summary.qualifying_folders}")
        self._append_log(f"Images in matching folders: {summary.qualifying_images}")
        if summary.stopped:
            self._append_log("Scan stopped by user before completion.")
            self.status_text.set("Scan stopped")
        else:
            self._append_log("Scan complete. No files or folders were changed.")
            self.status_text.set("Scan complete")

        self.summary_text.set(
            f"{summary.qualifying_folders} matches · {summary.qualifying_images} images"
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
