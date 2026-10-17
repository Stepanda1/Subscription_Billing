"""Deployment contracts checked against Compose's resolved configuration."""
import json
from pathlib import Path
import subprocess
import unittest


class ComposeContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = Path(__file__).resolve().parents[1]
        result = subprocess.run(
            ['docker', 'compose', '-f', str(root / 'compose.yaml'), 'config', '--format', 'json'],
            cwd=root, check=True, text=True, capture_output=True,
        )
        cls.services = json.loads(result.stdout)['services']

    def test_api_can_cold_start_without_rabbitmq(self):
        dependencies = self.services['api'].get('depends_on', {})
        self.assertNotIn('rabbitmq', dependencies,
                         'The outbox API must start when the broker is unavailable')
        self.assertEqual('service_completed_successfully', dependencies['migrate']['condition'])

    def test_stateful_dependencies_restart_with_the_application(self):
        for name in ('postgres', 'rabbitmq'):
            with self.subTest(service=name):
                self.assertEqual('unless-stopped', self.services[name].get('restart'),
                                 'A host restart must not leave only API/worker running')


if __name__ == '__main__':
    unittest.main()
