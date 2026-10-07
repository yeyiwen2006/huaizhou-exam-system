import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

class RepositoryReadinessTests(unittest.TestCase):
    def test_retained_document_links_resolve(self):
        names = subprocess.check_output(
            ['git', '-c', 'core.quotepath=false', 'ls-files', '--cached', '--others', '--exclude-standard'],
            cwd=ROOT, text=True, encoding='utf-8').splitlines()
        broken = []
        for name in names:
            path = ROOT / name
            if not path.is_file() or path.suffix != '.md':
                continue
            for match in re.finditer(r'\]\(([^)\s]+)', path.read_text(encoding='utf-8-sig')):
                target = match.group(1).split('#')[0]
                if target and not re.match(r'^[a-z]+:', target, re.I) and not (path.parent / target).exists():
                    broken.append((name, target))
        self.assertEqual([], broken)

    def test_all_acceptance_scenarios_are_retained(self):
        text = (ROOT / 'docs/acceptance-criteria.md').read_text(encoding='utf-8')
        scenarios = set(re.findall(r'^\|\s*(S\d{2})(?!\d)', text, re.MULTILINE))
        self.assertEqual({f'S{i:02}' for i in range(1, 22)}, scenarios)

    def test_development_only_artifacts_are_not_in_products(self):
        for name in ['AGENTS.md', 'development-plan.md', 'agent-implementation-guide.md',
                     'src/Exam.Server/appsettings.Development.json',
                     'src/Exam.Server/Properties/launchSettings.json',
                     'src/Exam.Server/Exam.Server.http',
                     'src/Exam.Admin/ViewModels/ManifestPreflightViewModel.cs',
                     'src/Exam.Desktop.Shared/Controls/ComponentShowcasePage.xaml']:
            with self.subTest(path=name):
                self.assertFalse((ROOT / name).exists())

if __name__ == '__main__':
    unittest.main()
