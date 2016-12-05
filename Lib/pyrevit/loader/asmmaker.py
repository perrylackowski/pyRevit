import clr
import os.path as op
from collections import namedtuple

import pyrevit.coreutils.appdata as appdata
from pyrevit import PYREVIT_ADDON_NAME, HOST_APP
from pyrevit.coreutils import join_strings, load_asm_file, find_loaded_asm
from pyrevit.coreutils.logger import get_logger
from pyrevit.repo import PYREVIT_VERSION

from pyrevit.loader import ASSEMBLY_FILE_TYPE
from pyrevit.loader.interfacetypes import make_cmd_classes, make_shared_classes

clr.AddReference('PresentationCore')
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
clr.AddReference('System.Xml.Linq')

# noinspection PyUnresolvedReferences
from System import AppDomain, Version
# noinspection PyUnresolvedReferences
from System.Reflection import Assembly, AssemblyName
# noinspection PyUnresolvedReferences
from System.Reflection.Emit import AssemblyBuilderAccess

# Generic named tuple for passing assembly information to other modules
ExtensionAssemblyInfo = namedtuple('ExtensionAssemblyInfo', ['name', 'location', 'reloading'])


logger = get_logger(__name__)


# fixme: change script executor to write to this log file (full address instead of name only)
SESSION_LOG_FILE = appdata.get_session_data_file('usagelog', 'log')
logger.debug('Generated log name for this session: {}'.format(SESSION_LOG_FILE))


# Generic named tuple for passing loader class parameters to the assembly maker
class CommandExecutorParams:
    def __init__(self, script_cmp):
        self.cmd_name = script_cmp.name
        self.cmd_context = script_cmp.cmd_context
        self.cmd_options = join_strings(script_cmp.get_cmd_options())
        self.class_name = script_cmp.unique_name
        self.avail_class_name = script_cmp.unique_avail_name
        self.script_file_address = script_cmp.get_full_script_address()
        self.config_script_file_address = script_cmp.get_full_config_script_address()
        self.search_paths_str = join_strings(script_cmp.get_search_paths())
        self.log_file = SESSION_LOG_FILE

    def __repr__(self):
        return '<Class:{} AvailClass:{} Name:{} Context:{}>'.format(self.class_name,
                                                                    self.avail_class_name,
                                                                    self.cmd_name,
                                                                    self.cmd_context)


def _make_ext_asm_name(extension):
    return '{}{}_{}_{}'.format(PYREVIT_ADDON_NAME, HOST_APP.version, extension.hash_value, extension.name)


def _make_ext_asm_fileid(extension):
    return '{}_{}'.format(extension.hash_value, extension.name)


def _is_pyrevit_ext_asm(asm_name, extension):
    # if this is a pyRevit package assembly
    return asm_name.startswith(PYREVIT_ADDON_NAME) and asm_name.endswith(extension.name)


def _is_pyrevit_ext_already_loaded(ext_asm_name):
    logger.debug('Asking Revit for previously loaded package assemblies: {}'.format(ext_asm_name))
    return find_loaded_asm(ext_asm_name) is not None


def _is_any_ext_asm_loaded(extension):
    for loaded_asm in AppDomain.CurrentDomain.GetAssemblies():
        logger.debug('Checking for loaded extension asm: {} ? {} : {}'.format(extension.name,
                                                                              loaded_asm.GetName().Name,
                                                                              loaded_asm))
        if _is_pyrevit_ext_asm(loaded_asm.GetName().Name, extension):
            return True
    return False


def _get_params_for_commands(parent_cmp):
    logger.debug('Creating a list of commands for the assembly maker from: {}'.format(parent_cmp))
    loader_params_for_all_cmds = []

    for sub_cmp in parent_cmp:
        if sub_cmp.is_container:
            loader_params_for_all_cmds.extend(_get_params_for_commands(sub_cmp))
        else:
            try:
                logger.debug('Command found: {}'.format(sub_cmp))
                loader_params_for_all_cmds.append(CommandExecutorParams(sub_cmp))
            except Exception as err:
                logger.debug('Can not create class parameters from: {} | {}'.format(sub_cmp, err))

    return loader_params_for_all_cmds


def _create_asm_file(extension):
    # make unique assembly name for this package
    ext_asm_name = _make_ext_asm_name(extension)
    # unique assembly filename for this package
    ext_asm_fileid = _make_ext_asm_fileid(extension)
    ext_asm_file_name = appdata.get_data_file(file_id=ext_asm_fileid,
                                              file_ext=ASSEMBLY_FILE_TYPE, name_only=True)
    ext_asm_file_path = appdata.get_data_file(file_id=ext_asm_fileid,
                                              file_ext=ASSEMBLY_FILE_TYPE)

    # check to see if any older assemblies have been loaded for this package
    # this means that we currently have this package loaded and we're reloading a new version
    is_reloading_pkg = _is_any_ext_asm_loaded(extension)

    # create assembly
    logger.debug('Building assembly for package: {}'.format(extension))
    pyrvt_ver_int_tuple = PYREVIT_VERSION.as_int_tuple()
    win_asm_name = AssemblyName(Name=ext_asm_name, Version=Version(pyrvt_ver_int_tuple[0],
                                                                   pyrvt_ver_int_tuple[1],
                                                                   pyrvt_ver_int_tuple[2], 0))
    logger.debug('Generated assembly name for this package: {0}'.format(ext_asm_name))
    logger.debug('Generated windows assembly name for this package: {0}'.format(win_asm_name))
    logger.debug('Generated assembly file name for this package: {0}'.format(ext_asm_file_name))
    asm_builder = AppDomain.CurrentDomain.DefineDynamicAssembly(win_asm_name,
                                                                AssemblyBuilderAccess.RunAndSave,
                                                                appdata.PYREVIT_APP_DIR)

    # get module builder
    module_builder = asm_builder.DefineDynamicModule(ext_asm_name, ext_asm_file_name)

    # create classes that could be called from any button (shared classes)
    # currently only default availability class is implemented. default availability class is for resetting
    # buttons back to normal context state (when reloading and after their context has changed during a session).
    make_shared_classes(module_builder)

    # create command classes
    for cmd_params in _get_params_for_commands(extension):  # type: CommandExecutorParams
        # create command executor class for this command
        logger.debug('Creating types for command: {}'.format(cmd_params))
        make_cmd_classes(module_builder, cmd_params)

    # save final assembly
    asm_builder.Save(ext_asm_file_name)
    logger.debug('Executer assembly saved.')
    return ExtensionAssemblyInfo(ext_asm_name, ext_asm_file_path, is_reloading_pkg)


def _produce_asm_file(extension):
    # make unique assembly name for this package
    ext_asm_name = _make_ext_asm_name(extension)
    # unique assembly filename for this package
    ext_asm_fileid = _make_ext_asm_fileid(extension)
    ext_asm_file_path = appdata.get_data_file(file_id=ext_asm_fileid,
                                              file_ext=ASSEMBLY_FILE_TYPE)

    if _is_pyrevit_ext_already_loaded(ext_asm_name):
        logger.debug('Extension assembly is already loaded: {}'.format(ext_asm_name))
        return ExtensionAssemblyInfo(ext_asm_name, ext_asm_file_path, True)
    elif appdata.is_data_file_available(file_id=ext_asm_fileid, file_ext=ASSEMBLY_FILE_TYPE):
        logger.debug('Extension assembly file already exists: {}'.format(ext_asm_file_path))
        load_asm_file(op.basename(ext_asm_file_path))
        return ExtensionAssemblyInfo(ext_asm_name, ext_asm_file_path, False)
    else:
        return _create_asm_file(extension)


def create_assembly(extension):
    """

    Args:
        extension (pyrevit.extensions.components.Extension):

    Returns:

    """
    logger.debug('Creating assembly for extension: {}'.format(extension.name))
    # create assembly file and return assembly file path to be used in UI creation
    # try:
    ext_asm_info = _produce_asm_file(extension)
    logger.debug('Assembly created: {}'.format(ext_asm_info))
    return ext_asm_info
    # except Exception as asm_err:
    #     logger.critical('Can not create assembly for: {} | {}'.format(extension, asm_err))
