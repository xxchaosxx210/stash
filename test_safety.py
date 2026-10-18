import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import processor as p
import scanner as s


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='stash-tests-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def source(self, name='A', extra=False):
        folder = self.root / name
        folder.mkdir()
        (folder / 'image2.jpg').write_bytes(b'original')
        if extra:
            (folder / 'note.txt').write_text('keep')
        return folder

    def plan(self, batch=500):
        rows = []
        s.scan_and_preview(self.root, 10, batch, 'Test_Misc',
                           on_folder=lambda f, q, v: rows.append((f, v)) if q else None)
        return p.build_copy_groups(self.root, rows)

    def test_existing_destination(self):
        self.source()
        groups = self.plan()
        item = groups[0].items[0]
        item.destination.parent.mkdir()
        item.destination.write_bytes(b'existing')
        with self.assertRaises(FileExistsError):
            p.process_groups(groups, True)
        self.assertEqual(item.destination.read_bytes(), b'existing')
        self.assertTrue(item.source.exists())

    def test_destination_appears_after_preflight(self):
        self.source()
        groups = self.plan()
        original = p._verified_copy
        def competing(source, destination):
            destination.write_bytes(b'competing')
            return original(source, destination)
        with patch.object(p, '_verified_copy', competing):
            with self.assertRaises(FileExistsError):
                p.process_groups(groups, True)
        self.assertEqual(groups[0].items[0].destination.read_bytes(), b'competing')
        self.assertTrue(groups[0].items[0].source.exists())

    def test_same_size_source_change_keeps_folder(self):
        folder = self.source()
        result = p.process_groups(self.plan(), True,
            on_copy=lambda item: item.source.write_bytes(b'modified'))
        self.assertTrue(folder.exists())
        self.assertEqual(result.source_folders_kept, 1)

    def test_destination_change_keeps_folder(self):
        folder = self.source()
        result = p.process_groups(self.plan(), True,
            on_copy=lambda item: item.destination.write_bytes(b'modified'))
        self.assertTrue(folder.exists())
        self.assertEqual(result.source_folders_kept, 1)

    def test_failure_prevents_all_deletion(self):
        self.source('A'); self.source('B')
        groups = self.plan()
        original = p._verified_copy
        def fail(source, destination):
            if source.parent.name == 'B':
                raise OSError('injected failure')
            return original(source, destination)
        with patch.object(p, '_verified_copy', fail):
            with self.assertRaises(OSError):
                p.process_groups(groups, True)
        self.assertTrue(all(i.source.exists() for g in groups for i in g.items))

    def test_safe_cleanup(self):
        folder = self.source()
        result = p.process_groups(self.plan(), True)
        self.assertFalse(folder.exists())
        self.assertEqual(result.source_folders_deleted, 1)

    def test_extras_kept(self):
        folder = self.source(extra=True)
        p.process_groups(self.plan(), True)
        self.assertTrue((folder / 'note.txt').exists())

    def test_old_prefix_excluded(self):
        folder = self.root / 'OldCustom_001'
        folder.mkdir()
        (folder / '000001__A__001.jpg').write_bytes(b'image')
        self.assertEqual(self.plan(), ())

    def test_normal_numbered_source_included(self):
        self.source('Holiday_001')
        self.assertEqual(len(self.plan()), 1)

    def test_sorting_grouping_and_repeat_numbering(self):
        folder = self.source()
        (folder / 'image10.jpg').write_bytes(b'ten')
        self.source('B'); self.source('C')
        groups = self.plan(batch=3)
        self.assertEqual(groups[0].items[0].source.name, 'image2.jpg')
        self.assertEqual(groups[0].items[0].destination.parent, groups[1].items[0].destination.parent)
        self.assertNotEqual(groups[1].items[0].destination.parent, groups[2].items[0].destination.parent)
        p.process_groups(groups, False)
        self.assertTrue(self.plan()[0].items[0].destination.name.startswith('000005__'))


if __name__ == '__main__':
    unittest.main()
