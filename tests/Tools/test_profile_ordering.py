"""Keep destructive native recovery from terminating a later profile's shared database."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'eng/testing'))
from evidence import expected_suites, load_plan
from run_checks import execution_suites


class ProfileOrderingTests(unittest.TestCase):
    def test_physical_runs_last_even_when_requested_before_import(self):
        plan = load_plan()
        profiles = ['postgres-physical', 'postgres', 'latex', 'import-e2e']
        actual = execution_suites(profiles, plan)
        self.assertEqual(expected_suites(profiles, plan), actual)
        self.assertEqual('postgres-physical-Exam.IntegrationTests', list(actual)[-1])
        self.assertLess(list(actual).index('import-e2e-Exam.IntegrationTests'),
                        list(actual).index('postgres-physical-Exam.IntegrationTests'))

    def test_all_classifications_and_filters_are_preserved_once(self):
        plan = load_plan()
        profiles = list(plan['profiles'])
        actual = execution_suites(profiles, plan)
        expected = expected_suites(profiles, plan)
        self.assertEqual(expected, actual)
        ordinary = [key for key in expected if not key.startswith('postgres-physical-')]
        self.assertEqual(ordinary, [key for key in actual if not key.startswith('postgres-physical-')])
        self.assertEqual(len(expected), len(actual))

    def test_without_physical_profile_requested_order_is_unchanged(self):
        plan = load_plan()
        profiles = ['import-e2e', 'core', 'wpf', 'postgres']
        self.assertEqual(list(expected_suites(profiles, plan).items()),
                         list(execution_suites(profiles, plan).items()))
