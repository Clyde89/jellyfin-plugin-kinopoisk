import unittest
from release_control import updated_catalog, retirement_target


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.catalog = [{'guid': 'plugin', 'name': 'КиноПоиск', 'versions': [{'version': 'old', 'checksum': 'previous'}]},
                        {'guid': 'other', 'versions': []}]
        self.metadata = {'guid': 'plugin', 'overview': 'Новые возможности', 'description': 'Описание'}
        self.entry = {'version': 'new', 'checksum': 'archive', 'timestamp': 'today'}

    def test_preserves_previous_versions_other_plugins_and_input(self):
        result = updated_catalog(self.catalog, self.metadata, self.entry)
        self.assertEqual(result[0]['versions'], [self.entry, self.catalog[0]['versions'][0]])
        self.assertEqual(result[1], self.catalog[1])
        self.assertEqual(len(self.catalog[0]['versions']), 1)
        self.assertEqual(result[0]['name'], 'КиноПоиск')
        self.assertEqual(result[0]['overview'], self.metadata['overview'])
        self.assertEqual(result[0]['description'], self.metadata['description'])

    def test_idempotent_retry_keeps_original_timestamp(self):
        first = updated_catalog(self.catalog, self.metadata, self.entry)
        self.assertEqual(updated_catalog(first, self.metadata, dict(self.entry, timestamp='later')), first)

    def test_conflicting_checksum_is_rejected(self):
        first = updated_catalog(self.catalog, self.metadata, self.entry)
        with self.assertRaises(ValueError):
            updated_catalog(first, self.metadata, dict(self.entry, checksum='different'))

    def test_missing_or_duplicate_plugin_is_rejected(self):
        for catalog in [[], [self.catalog[0], self.catalog[0]]]:
            with self.assertRaises(ValueError):
                updated_catalog(catalog, self.metadata, self.entry)


class RetirementTests(unittest.TestCase):
    def test_protected_and_default_branches_are_rejected(self):
        for branch in ['develop', 'master', 'release', 'main', 'fix/default']:
            with self.assertRaises(ValueError):
                retirement_target({'name': branch, 'sha': 'a' * 40}, 'b' * 40, 'fix/default')

    def test_unexpected_names_and_short_sha_are_rejected(self):
        for entry in [{'name': 'refs/heads/fix/test', 'sha': 'a' * 40}, {'name': 'fix/test', 'sha': 'abc'}]:
            with self.assertRaises(ValueError):
                retirement_target(entry, 'b' * 40, 'develop')

    def test_release_branch_requires_exact_published_sha(self):
        self.assertEqual(retirement_target({'name': 'fix/review', 'sha': 'a' * 40, 'releaseBranch': True},
                                           'b' * 40, 'develop'), ('fix/review', 'b' * 40))


if __name__ == '__main__':
    unittest.main()
