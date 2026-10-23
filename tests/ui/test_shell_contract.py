"""Portable XAML wiring contracts; these do not claim WPF rendering or game acceptance."""
import copy
import hashlib
import json
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
LAUNCHER = ROOT / 'src/launcher/src'
DONOR = ROOT / 'integration/scbl3/client/ScblPublicLauncher/MainWindow.xaml'
P = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
CONTRACT = json.loads(Path(__file__).with_name('lan-shell-contract.json').read_text())
EVENTS = {'Click', 'Loaded', 'Closing', 'SelectionChanged', 'Checked', 'Unchecked',
          'ValueChanged', 'PreviewTextInput', 'DataObject.Pasting', 'TextChanged',
          'LostKeyboardFocus', 'PasswordChanged', 'DropDownOpened',
          'PreviewGotKeyboardFocus', 'PreviewKeyDown', 'MouseLeftButtonDown', 'Opened'}


def window_controls(root):
    """Exclude template names: WPF gives templates their own namescopes."""
    root = copy.deepcopy(root)
    resources = root.find(P + 'Window.Resources')
    if resources is not None:
        root.remove(resources)
    result = {}
    for element in root.iter():
        name = element.get(X + 'Name')
        if name:
            if name in result:
                raise AssertionError('duplicate window control: ' + name)
            result[name] = element
    return result


def validate_retained_routes(root):
    controls = window_controls(root)
    for name, original in CONTRACT['controls'].items():
        if name not in controls:
            raise AssertionError('missing retained control: ' + name)
        control = controls[name]
        if control.tag != P + original['type']:
            raise AssertionError('retained control type changed: ' + name)
        for event, handler in original['events'].items():
            if control.get(event) != handler:
                raise AssertionError('retained event changed: ' + name + '.' + event)
    if root.get('Loaded') != 'Window_Loaded' or root.get('Closing') != 'Window_Closing':
        raise AssertionError('retained lifecycle changed')
    return controls


class ShellContractTests(unittest.TestCase):
    def setUp(self):
        self.root = ET.parse(LAUNCHER / 'MainWindow.xaml').getroot()
        self.controls = window_controls(self.root)

    def test_retains_every_lan_window_control_type_and_event(self):
        validate_retained_routes(self.root)
        self.assertEqual(len(CONTRACT['controls']), 103)

    def test_rejects_removed_launch_recovery_observer_or_save_controls(self):
        for name in ['btnLaunch', 'recoveryCode', 'chkObserverMode', 'btnRestoreOfficial', 'btnRestoreMod']:
            with self.subTest(name=name):
                altered = copy.deepcopy(self.root)
                # Find the real element, since window_controls deliberately returns a copy.
                for parent in altered.iter():
                    for child in list(parent):
                        if child.get(X + 'Name') == name:
                            parent.remove(child)
                with self.assertRaisesRegex(AssertionError, 'missing retained control'):
                    validate_retained_routes(altered)

    def test_rejects_disconnected_launch_export_recovery_and_observer_events(self):
        for name, event in [('btnLaunch', 'Click'), ('btnExportDiagnostics', 'Click'),
                            ('btnResetPassword', 'Click'), ('chkObserverMode', 'Checked')]:
            with self.subTest(name=name):
                altered = copy.deepcopy(self.root)
                for element in altered.iter():
                    if element.get(X + 'Name') == name:
                        element.set(event, 'UnsafeReplacement_Click')
                with self.assertRaisesRegex(AssertionError, 'retained event changed'):
                    validate_retained_routes(altered)

    def test_rejects_donor_automatic_network_loaded_handler(self):
        altered = copy.deepcopy(self.root)
        altered.set('Loaded', 'TestChannelAwareWindow_Loaded')
        with self.assertRaisesRegex(AssertionError, 'retained lifecycle changed'):
            validate_retained_routes(altered)

    def test_each_event_resolves_to_exactly_one_main_window_method(self):
        code = '\n'.join(path.read_text(encoding='utf-8-sig') for path in LAUNCHER.glob('MainWindow*.cs'))
        for element in self.root.iter():
            for event, handler in element.attrib.items():
                if event not in EVENTS:
                    continue
                declaration = r'\b(?:private|public|internal|protected)\s+(?:async\s+)?(?:void|Task|bool)\s+' + re.escape(handler) + r'\s*\('
                with self.subTest(handler=handler):
                    self.assertEqual(len(re.findall(declaration, code)), 1)

    def test_no_donor_update_network_or_authfile_routes(self):
        handlers = {value for element in self.root.iter() for key, value in element.attrib.items() if key in EVENTS}
        self.assertFalse(handlers & {'TestChannelAwareWindow_Loaded', 'TestChannelMenuItem_Click',
                                    'NetworkMode_SelectionChanged', 'RepairNetworkMenuItem_Click',
                                    'OverwriteSavesMenuItem_Click'})
        partial = LAUNCHER / 'MainWindow.DonorShell.cs'
        if partial.exists():
            code = partial.read_text()
            for forbidden in ['NetworkOrchestrator', 'WriteAuthFile', 'Process.Start', 'HttpClient', 'SaveSettingsFromUi']:
                self.assertNotIn(forbidden, code)

    def test_shell_adopts_donor_menu_and_footer_styles(self):
        donor = ET.parse(DONOR).getroot()
        donor_keys = {element.get(X + 'Key') for element in donor.iter()}
        keys = {element.get(X + 'Key') for element in self.root.iter()}
        for key in ['SettingsMenuItemStyle', 'SettingsContextMenuStyle', 'BottomStatusButton']:
            self.assertIn(key, donor_keys)
            self.assertIn(key, keys)
        self.assertEqual(self.controls['settingsMenu'].get('Style'), '{StaticResource SettingsContextMenuStyle}')
        for name, handler in [('miLanSettings', 'SettingsButton_Click'), ('miRestoreSaves', 'RestoreSavesButton_Click'),
                              ('miPlayers', 'PlayersButton_Click'), ('miExportDiagnostics', 'ExportDiagnosticsMenuItem_Click')]:
            self.assertEqual(self.controls[name].get('Click'), handler)

    def test_donor_snapshot_and_transplanted_resources_are_preserved(self):
        self.assertEqual(hashlib.sha256(DONOR.read_bytes()).hexdigest(), CONTRACT['donor_xaml_sha256'])
        donor = ET.parse(DONOR).getroot()
        def shape(element):
            return (element.tag, sorted(element.attrib.items()), (element.text or '').strip(),
                    tuple(shape(child) for child in element))
        donor_resources = {e.get(X + 'Key'): e for e in donor.find(P + 'Window.Resources')}
        resources = {e.get(X + 'Key'): e for e in self.root.find(P + 'Window.Resources')}
        for key in ['SettingsMenuItemStyle', 'SettingsContextMenuStyle', 'BottomStatusButton']:
            self.assertIn(key, resources)
            self.assertEqual(shape(resources[key]), shape(donor_resources[key]))

    def test_environment_is_separate_from_lan_role_and_public_is_unavailable(self):
        environments = self.controls['cmbNetworkEnvironment']
        self.assertEqual(environments.get('SelectedValue'), 'Lan')
        self.assertIsNone(environments.get('SelectionChanged'))
        choices = {item.get('Tag'): item for item in environments if item.tag == P + 'ListBoxItem'}
        self.assertEqual(set(choices), {'Lan', 'Public'})
        self.assertEqual(choices['Public'].get('IsEnabled'), 'False')
        self.assertEqual(self.controls['cmbConnectionMode'].get('SelectionChanged'), 'ConnectionMode_SelectionChanged')
        self.assertEqual([item.get(X + 'Name') for item in self.controls['cmbConnectionMode']], ['itemClientMode', 'itemHostMode'])

    def test_renderer_remains_dx11_only_and_fov_contract_is_preserved(self):
        renderer = self.controls['cmbGameExecutable']
        self.assertEqual(renderer.get('SelectedValue'), 'DX11')
        options = {item.get('Tag'): item for item in renderer if item.tag == P + 'ListBoxItem'}
        self.assertEqual(options['DX9'].get('IsEnabled'), 'False')
        self.assertIsNone(renderer.get('SelectionChanged'))
        slider = self.controls['fovSlider']
        self.assertEqual((slider.get('Minimum'), slider.get('Maximum'), slider.get('Value')), ('1.0', '1.3', '1.2'))
        self.assertEqual(slider.get('ValueChanged'), 'FovSlider_ValueChanged')

    def test_local_static_resources_resolve_and_names_are_unique(self):
        keys = [element.get(X + 'Key') for element in self.root.iter() if element.get(X + 'Key')]
        self.assertEqual(len(keys), len(set(keys)))
        for element in self.root.iter():
            for value in element.attrib.values():
                for key in re.findall(r'\{StaticResource ([^}]+)\}', value):
                    self.assertIn(key, keys)
        window_controls(self.root)

    def test_original_focus_boundaries_and_overlays_are_retained(self):
        for name in ['toolsOverlay', 'dialogOverlay']:
            self.assertEqual(self.controls[name].get('KeyboardNavigation.TabNavigation'), 'Cycle')
            self.assertEqual(self.controls[name].get('Visibility'), 'Collapsed')
        self.assertEqual(self.controls['rootGrid'].get('PreviewGotKeyboardFocus'), 'DialogBoundary_PreviewGotKeyboardFocus')
        self.assertEqual(self.controls['rootGrid'].get('PreviewKeyDown'), 'DialogOverlay_PreviewKeyDown')


if __name__ == '__main__':
    unittest.main()
