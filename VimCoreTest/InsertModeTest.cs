﻿using System;
using System.Collections.Generic;
using System.Linq;
using EditorUtils.UnitTest;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Moq;
using NUnit.Framework;
using Vim.Extensions;
using Vim.UnitTest.Mock;
using Vim.Modes.Insert;

namespace Vim.UnitTest
{
    /// <summary>
    /// Tests to verify the operation of Insert / Replace Mode
    /// </summary>
    [TestFixture]
    public sealed class InsertModeTest : VimTestBase
    {
        private MockRepository _factory;
        private InsertMode _modeRaw;
        private IInsertMode _mode;
        private ITextView _textView;
        private ITextBuffer _textBuffer;
        private CommandRunData _lastCommandRan;
        private Mock<ICommonOperations> _operations;
        private Mock<IDisplayWindowBroker> _broker;
        private Mock<IEditorOptions> _editorOptions;
        private Mock<IUndoRedoOperations> _undoRedoOperations;
        private Mock<ITextChangeTracker> _textChangeTracker;
        private Mock<IInsertUtil> _insertUtil;
        private Mock<IKeyboardDevice> _keyboardDevice;
        private Mock<IMouseDevice> _mouseDevice;
        private Mock<IVim> _vim;
        private Mock<IVimBuffer> _vimBuffer;
        private Mock<IWordCompletionSessionFactoryService> _wordCompletionSessionFactoryService;
        private Mock<IWordCompletionSession> _activeWordCompletionSession;

        [SetUp]
        public void SetUp()
        {
            Create(insertMode: true);
        }

        [TearDown]
        public void TearDown()
        {
            _lastCommandRan = null;
        }

        private void Create(params string[] lines)
        {
            Create(true, lines);
        }

        private void Create(bool insertMode, params string[] lines)
        {
            _factory = new MockRepository(MockBehavior.Strict);
            _factory.DefaultValue = DefaultValue.Mock;
            _textView = CreateTextView(lines);
            _textBuffer = _textView.TextBuffer;
            _vim = _factory.Create<IVim>(MockBehavior.Loose);
            _editorOptions = _factory.Create<IEditorOptions>(MockBehavior.Loose);
            _textChangeTracker = _factory.Create<ITextChangeTracker>(MockBehavior.Loose);
            _textChangeTracker.SetupGet(x => x.CurrentChange).Returns(FSharpOption<TextChange>.None);
            _undoRedoOperations = _factory.Create<IUndoRedoOperations>();
            _wordCompletionSessionFactoryService = _factory.Create<IWordCompletionSessionFactoryService>();

            var localSettings = new LocalSettings(Vim.GlobalSettings);
            _vimBuffer = MockObjectFactory.CreateVimBuffer(
                _textView,
                localSettings: localSettings,
                vim: _vim.Object,
                factory: _factory);
            _vimBuffer.SetupGet(x => x.ModeKind).Returns(ModeKind.Insert);
            _operations = _factory.Create<ICommonOperations>();
            _operations.SetupGet(x => x.EditorOperations).Returns(EditorOperationsFactoryService.GetEditorOperations(_textView));
            _broker = _factory.Create<IDisplayWindowBroker>();
            _broker.SetupGet(x => x.IsCompletionActive).Returns(false);
            _broker.SetupGet(x => x.IsQuickInfoActive).Returns(false);
            _broker.SetupGet(x => x.IsSignatureHelpActive).Returns(false);
            _insertUtil = _factory.Create<IInsertUtil>();

            // Setup the mouse.  By default we say it has no buttons down as that's the normal state
            _mouseDevice = _factory.Create<IMouseDevice>();
            _mouseDevice.SetupGet(x => x.IsLeftButtonPressed).Returns(false);

            // Setup the keyboard.  By default we don't say that any button is pressed.  Insert mode is usually
            // only concerned with arrow keys and we will set those up as appropriate for the typing tests
            _keyboardDevice = _factory.Create<IKeyboardDevice>();
            _keyboardDevice.Setup(x => x.IsKeyDown(It.IsAny<VimKey>())).Returns(false);

            _modeRaw = new global::Vim.Modes.Insert.InsertMode(
                _vimBuffer.Object,
                _operations.Object,
                _broker.Object,
                _editorOptions.Object,
                _undoRedoOperations.Object,
                _textChangeTracker.Object,
                _insertUtil.Object,
                !insertMode,
                _keyboardDevice.Object,
                _mouseDevice.Object,
                WordUtilFactory.GetWordUtil(_textView.TextBuffer),
                _wordCompletionSessionFactoryService.Object);
            _mode = _modeRaw;
            _mode.CommandRan += (sender, e) => { _lastCommandRan = e.CommandRunData; };
        }

        private void SetupMoveCaretLeft()
        {
            _insertUtil
                .Setup(x => x.RunInsertCommand(InsertCommand.NewMoveCaret(Direction.Left)))
                .Returns(CommandResult.NewCompleted(ModeSwitch.NewSwitchMode(ModeKind.Normal)))
                .Verifiable();
        }

        private void SetupActiveWordCompletionSession()
        {
            _activeWordCompletionSession = _factory.Create<IWordCompletionSession>(MockBehavior.Loose);
            _wordCompletionSessionFactoryService
                .Setup(x => x.CreateWordCompletionSession(_textView, It.IsAny<SnapshotSpan>(), It.IsAny<IEnumerable<string>>(), It.IsAny<bool>()))
                .Returns(_activeWordCompletionSession.Object);
            _modeRaw.StartWordCompletionSession(true);
        }

        private void SetupInsertCommand(InsertCommand command)
        {
            _insertUtil
                .Setup(x => x.RunInsertCommand(command))
                .Returns(CommandResult.NewCompleted(ModeSwitch.NoSwitch))
                .Verifiable();
        }

        /// <summary>
        /// If the active IWordCompletionSession is dismissed via the API it should cause the 
        /// ActiveWordCompletionSession value to be reset as well
        /// </summary>
        [Test]
        public void ActiveWordCompletionSession_Dismissed()
        {
            Create("");
            SetupActiveWordCompletionSession();
            _activeWordCompletionSession.Raise(x => x.Dismissed += null, EventArgs.Empty);
            Assert.IsTrue(_mode.ActiveWordCompletionSession.IsNone());
        }

        /// <summary>
        /// Make sure we can process escape
        /// </summary>
        [Test]
        public void CanProcess_Escape()
        {
            Assert.IsTrue(_mode.CanProcess(KeyInputUtil.EscapeKey));
        }

        /// <summary>
        /// When there is an active IWordCompletionSession we should still process all input even though 
        /// the word completion session can only process a limited set of key strokes.  The extra key 
        /// strokes are used to cancel the session and then be processed as normal
        /// </summary>
        [Test]
        public void CanProcess_ActiveWordCompletion()
        {
            Create("");
            SetupActiveWordCompletionSession();
            Assert.IsTrue(_mode.CanProcess(KeyInputUtil.CharToKeyInput('a')));
        }

        /// <summary>
        /// After a word should return the entire word 
        /// </summary>
        [Test]
        public void GetWordCompletionSpan_AfterWord()
        {
            Create("cat dog");
            _textView.MoveCaretTo(3);
            Assert.AreEqual("cat", _modeRaw.GetWordCompletionSpan().Value.GetText());
        }

        /// <summary>
        /// In the middle of the word should only consider the word up till the caret for the 
        /// completion section
        /// </summary>
        [Test]
        public void GetWordCompletionSpan_MiddleOfWord()
        {
            Create("cat dog");
            _textView.MoveCaretTo(1);
            Assert.AreEqual("c", _modeRaw.GetWordCompletionSpan().Value.GetText());
        }

        /// <summary>
        /// When the caret is on a closing paren and after a word the completion should be for the
        /// word and not for the paren
        /// </summary>
        [Test]
        public void GetWordCompletionSpan_OnParen()
        {
            Create("m(arg)");
            _textView.MoveCaretTo(5);
            Assert.AreEqual(')', _textView.GetCaretPoint().GetChar());
            Assert.AreEqual("arg", _modeRaw.GetWordCompletionSpan().Value.GetText());
        }

        /// <summary>
        /// This is a sanity check to make sure we don't try anything like jumping backwards.  The 
        /// test should be for the character immediately preceding the caret position.  Here it's 
        /// a blank and there should be nothing returned
        /// </summary>
        [Test]
        public void GetWordCompletionSpan_OnParenWithBlankBefore()
        {
            Create("m(arg )");
            _textView.MoveCaretTo(6);
            Assert.AreEqual(')', _textView.GetCaretPoint().GetChar());
            Assert.IsTrue(_modeRaw.GetWordCompletionSpan().IsNone());
        }

        /// <summary>
        /// When provided an empty SnapshotSpan the words should be returned in order from the given
        /// point
        /// </summary>
        [Test]
        public void GetWordCompletions_All()
        {
            Create("cat dog tree");
            var words = _modeRaw.GetWordCompletions(new SnapshotSpan(_textView.TextSnapshot, 3, 0));
            CollectionAssert.AreEquivalent(
                new[] { "dog", "tree", "cat" },
                words.ToList());
        }

        /// <summary>
        /// Don't include any comments or non-words when getting the words from the buffer
        /// </summary>
        [Test]
        public void GetWordCompletions_All_JustWords()
        {
            Create("cat dog // tree &&");
            var words = _modeRaw.GetWordCompletions(new SnapshotSpan(_textView.TextSnapshot, 3, 0));
            CollectionAssert.AreEquivalent(
                new[] { "dog", "tree", "cat" },
                words.ToList());
        }

        /// <summary>
        /// When given a word span only include strings which start with the given prefix
        /// </summary>
        [Test]
        public void GetWordCompletions_Prefix()
        {
            Create("c cat dog // tree && copter");
            var words = _modeRaw.GetWordCompletions(new SnapshotSpan(_textView.TextSnapshot, 0, 1));
            CollectionAssert.AreEquivalent(
                new[] { "cat", "copter" },
                words.ToList());
        }

        /// <summary>
        /// Starting from the middle of a word should consider the part of the word to the right of 
        /// the caret as a word
        /// </summary>
        [Test]
        public void GetWordCompletions_MiddleOfWord()
        {
            Create("test", "ccrook cat caturday");
            var words = _modeRaw.GetWordCompletions(new SnapshotSpan(_textView.GetLine(1).Start, 1));
            CollectionAssert.AreEquivalent(
                new[] { "cat", "caturday", "crook" },
                words.ToList());
        }

        /// <summary>
        /// Don't include any one length values in the return because Vim doesn't include them
        /// </summary>
        [Test]
        public void GetWordCompletions_ExcludeOneLengthValues()
        {
            Create("c cat dog // tree && copter a b c");
            var words = _modeRaw.GetWordCompletions(new SnapshotSpan(_textView.TextSnapshot, 0, 1));
            CollectionAssert.AreEquivalent(
                new[] { "cat", "copter" },
                words.ToList());
        }

        /// <summary>
        /// Ensure that all known character values are considered direct input.  They cause direct
        /// edits to the buffer.  They are not commands.
        /// </summary>
        [Test]
        public void IsDirectInput_Chars()
        {
            foreach (var cur in KeyInputUtilTest.CharsAll)
            {
                var input = KeyInputUtil.CharToKeyInput(cur);
                Assert.IsTrue(_mode.CanProcess(input));
                Assert.IsTrue(_mode.IsDirectInsert(input));
            }
        }

        /// <summary>
        /// Certain keys do cause buffer edits but are not direct input.  They are interpreted by Vim
        /// and given specific values based on settings.  While they cause edits the values passed down
        /// don't directly go to the buffer
        /// </summary>
        [Test]
        public void IsDirectInput_SpecialKeys()
        {
            Assert.IsFalse(_mode.IsDirectInsert(KeyInputUtil.EnterKey));
            Assert.IsFalse(_mode.IsDirectInsert(KeyInputUtil.AlternateEnterKey));
            Assert.IsFalse(_mode.IsDirectInsert(KeyInputUtil.VimKeyToKeyInput(VimKey.Tab)));
        }

        /// <summary>
        /// Make sure to move the caret left when exiting insert mode
        /// </summary>
        [Test]
        public void Escape_MoveCaretLeftOnExit()
        {
            _textView.SetText("hello world", 3);
            _broker.SetupGet(x => x.IsCompletionActive).Returns(false).Verifiable();
            _broker.SetupGet(x => x.IsQuickInfoActive).Returns(false).Verifiable();
            _broker.SetupGet(x => x.IsSignatureHelpActive).Returns(false).Verifiable();
            SetupMoveCaretLeft();
            var res = _mode.Process(KeyInputUtil.EscapeKey);
            Assert.IsTrue(res.IsSwitchMode(ModeKind.Normal));
            _factory.Verify();
        }

        /// <summary>
        /// Make sure to dismiss any active completion windows when exiting.  We had the choice
        /// between having escape cancel only the window and escape canceling and returning
        /// to presambly normal mode.  The unanimous user feedback is that Escape should leave 
        /// insert mode no matter what.  
        /// </summary>
        [Test]
        public void Escape_DismissCompletionWindows()
        {
            _textView.SetText("hello world", 1);
            _broker
                .SetupGet(x => x.IsCompletionActive)
                .Returns(true)
                .Verifiable();
            _broker
                .Setup(x => x.DismissDisplayWindows())
                .Verifiable();
            SetupMoveCaretLeft();
            var res = _mode.Process(KeyInputUtil.EscapeKey);
            Assert.IsTrue(res.IsSwitchMode(ModeKind.Normal));
            _factory.Verify();
        }

        /// <summary>
        /// If the caret is in virtual space when leaving insert mode move it back to the real
        /// position.  This really only comes up in a few cases, primarily the 'C' command 
        /// which preserves indent by putting the caret in virtual space.  For example take the 
        /// following (- are spaces and ^ is caret).
        /// --cat
        ///
        /// Caret starts on the 'c' and 'autoindent' is on.  Execute the following
        ///  - cc
        ///  - Escape
        /// Now the caret is at position 0 on a blank line 
        /// </summary>
        [Test]
        public void Escape_LeaveVirtualSpace()
        {
            _textView.SetText("", "random data");
            var virtualPoint = new VirtualSnapshotPoint(_textView.TextSnapshot.GetPoint(0), 2);
            _textView.Caret.MoveTo(virtualPoint);
            _operations.Setup(x => x.MoveCaretToPoint(virtualPoint.Position)).Verifiable();
            _mode.Process(KeyInputUtil.EscapeKey);
            _operations.Verify();
        }

        [Test]
        public void Control_OpenBracket1()
        {
            var ki = KeyInputUtil.CharWithControlToKeyInput('[');
            var name = KeyInputSet.NewOneKeyInput(ki);
            Assert.IsTrue(_mode.CommandNames.Contains(name));
        }

        [Test]
        public void Control_OpenBracket2()
        {
            _broker
                .SetupGet(x => x.IsCompletionActive)
                .Returns(true)
                .Verifiable();
            _broker
                .Setup(x => x.DismissDisplayWindows())
                .Verifiable();
            _insertUtil
                .Setup(x => x.RunInsertCommand(InsertCommand.NewMoveCaret(Direction.Left)))
                .Returns(CommandResult.NewCompleted(ModeSwitch.NewSwitchMode(ModeKind.Normal)))
                .Verifiable();
            var ki = KeyInputUtil.CharWithControlToKeyInput('[');
            var res = _mode.Process(ki);
            Assert.IsTrue(res.IsSwitchMode(ModeKind.Normal));
            _factory.Verify();
        }

        /// <summary>
        /// Make sure we bind the shift left command
        /// </summary>
        [Test]
        public void Command_ShiftLeft()
        {
            _textView.SetText("hello world");
            _insertUtil.Setup(x => x.RunInsertCommand(InsertCommand.ShiftLineLeft)).Returns(CommandResult.NewCompleted(ModeSwitch.NoSwitch)).Verifiable();
            var res = _mode.Process(KeyInputUtil.CharWithControlToKeyInput('d'));
            Assert.IsTrue(res.IsHandledNoSwitch());
            _factory.Verify();
        }

        /// <summary>
        /// Make sure we bind the shift right command
        /// </summary>
        [Test]
        public void Command_ShiftRight()
        {
            SetUp();
            _textView.SetText("hello world");
            _insertUtil.Setup(x => x.RunInsertCommand(InsertCommand.ShiftLineRight)).Returns(CommandResult.NewCompleted(ModeSwitch.NoSwitch)).Verifiable();
            _mode.Process(KeyNotationUtil.StringToKeyInput("<C-T>"));
            _factory.Verify();
        }

        [Test]
        public void OnLeave1()
        {
            _mode.OnLeave();
            _factory.Verify();
        }

        /// <summary>
        /// The CTRL-O command should bind to a one time command for normal mode
        /// </summary>
        [Test]
        public void OneTimeCommand()
        {
            var res = _mode.Process(KeyNotationUtil.StringToKeyInput("<C-o>"));
            Assert.IsTrue(res.IsSwitchModeOneTimeCommand());
        }

        [Test]
        public void ReplaceMode1()
        {
            Create(insertMode: false);
            Assert.AreEqual(ModeKind.Replace, _mode.ModeKind);
        }

        [Test]
        public void ReplaceMode2()
        {
            Create(insertMode: false);
            _editorOptions
                .Setup(x => x.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, false))
                .Verifiable();
            _mode.OnLeave();
            _factory.Verify();
        }

        /// <summary>
        /// When the caret moves due to the mouse being clicked that should complete the current text change
        /// </summary>
        [Test]
        public void TextChange_CaretMoveFromClickShouldComplete()
        {
            Create("the quick brown fox");
            _textBuffer.Insert(0, "a");
            _textChangeTracker.Setup(x => x.CompleteChange()).Verifiable();
            _mouseDevice.SetupGet(x => x.IsLeftButtonPressed).Returns(true).Verifiable();
            _textView.MoveCaretTo(7);
            _factory.Verify();
        }

        /// <summary>
        /// When the caret moves as a part of the edit then it shouldn't cause the change to complete
        /// </summary>
        [Test]
        public void TextChange_CaretMoveFromEdit()
        {
            Create("the quick brown fox");
            _textChangeTracker.Setup(x => x.CompleteChange()).Throws(new Exception());
            _mouseDevice.SetupGet(x => x.IsLeftButtonPressed).Returns(false).Verifiable();
            _textBuffer.Insert(0, "a");
            _textView.MoveCaretTo(7);
            _factory.Verify();
        }

        /// <summary>
        /// Ensure that CTRL-N is mapped to MoveNext in the IWordCompletionSession
        /// </summary>
        [Test]
        public void Process_WordCompletion_CtrlN()
        {
            Create("hello world");
            SetupActiveWordCompletionSession();
            _activeWordCompletionSession.Setup(x => x.MoveNext()).Returns(true).Verifiable();
            _mode.Process(KeyNotationUtil.StringToKeyInput("<C-n>"));
            _activeWordCompletionSession.Verify();
        }

        /// <summary>
        /// Ensure that down is mapped to MoveeNext in the IWordCompletionSession
        /// </summary>
        [Test]
        public void Process_WordCompletion_Down()
        {
            Create("hello world");
            SetupActiveWordCompletionSession();
            _activeWordCompletionSession.Setup(x => x.MoveNext()).Returns(true).Verifiable();
            _mode.Process(VimKey.Down);
            _activeWordCompletionSession.Verify();
        }

        /// <summary>
        /// Ensure that CTRL-N is mapped to MovePrevious in the IWordCompletionSession
        /// </summary>
        [Test]
        public void Process_WordCompletion_CtrlP()
        {
            Create("hello world");
            SetupActiveWordCompletionSession();
            _activeWordCompletionSession.Setup(x => x.MovePrevious()).Returns(true).Verifiable();
            _mode.Process(KeyNotationUtil.StringToKeyInput("<C-p>"));
            _activeWordCompletionSession.Verify();
        }

        /// <summary>
        /// Ensure that up is mapped to MovePrevious in the IWordCompletionSession
        /// </summary>
        [Test]
        public void Process_WordCompletion_Up()
        {
            Create("hello world");
            SetupActiveWordCompletionSession();
            _activeWordCompletionSession.Setup(x => x.MovePrevious()).Returns(true).Verifiable();
            _mode.Process(VimKey.Up);
            _activeWordCompletionSession.Verify();
        }

        /// <summary>
        /// Typing any character should cause the IWordCompletionSession to be dismissed and 
        /// be processed as a normal char
        /// </summary>
        [Test]
        public void Process_WordCompletion_Char()
        {
            Create("hello world");
            SetupActiveWordCompletionSession();
            SetupInsertCommand(InsertCommand.NewDirectInsert('c'));
            _textView.MoveCaretTo(0);
            _activeWordCompletionSession.Setup(x => x.Dismiss()).Verifiable();
            _mode.Process('c');
            _activeWordCompletionSession.Verify();
            _insertUtil.Verify();
            Assert.IsTrue(_mode.ActiveWordCompletionSession.IsNone());
        }

        /// <summary>
        /// Ensure that Enter maps to the appropriate InsertCommand and shows up as the LastCommand
        /// after processing
        /// </summary>
        [Test]
        public void Process_InsertNewLine()
        {
            Create("");
            SetupInsertCommand(InsertCommand.InsertNewLine);
            _mode.Process(VimKey.Enter);
            _insertUtil.Verify();
            Assert.IsTrue(_modeRaw._sessionData.CombinedEditCommand.IsSome());
            Assert.IsTrue(_modeRaw._sessionData.CombinedEditCommand.Value.IsInsertNewLine);
        }

        /// <summary>
        /// Ensure that a character maps to the DirectInsert and shows up as the LastCommand
        /// after processing
        /// </summary>
        [Test]
        public void Process_DirectInsert()
        {
            Create("");
            SetupInsertCommand(InsertCommand.NewDirectInsert('c'));
            _mode.Process('c');
            _insertUtil.Verify();
            Assert.IsTrue(_modeRaw._sessionData.CombinedEditCommand.IsSome());
            Assert.IsTrue(_modeRaw._sessionData.CombinedEditCommand.Value.IsDirectInsert);
        }
    }
}
