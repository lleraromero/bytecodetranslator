﻿//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.Cci;
using Microsoft.Cci.MetadataReader;
using Microsoft.Cci.MutableCodeModel;
using Microsoft.Cci.Contracts;
using Microsoft.Cci.ILToCodeModel;

using Bpl = Microsoft.Boogie;

namespace BytecodeTranslator {

  public class CLRSemantics : TraverserFactory {

    public override ExpressionTraverser MakeExpressionTraverser(StatementTraverser parent, Bpl.Variable heapVariable) {
      return new CLRExpressionSemantics(parent, heapVariable);
    }

    public class CLRExpressionSemantics : ExpressionTraverser {

      public CLRExpressionSemantics(StatementTraverser stmtTraverser, Bpl.Variable heapvar)
        : base(stmtTraverser, heapvar) { }

      public override void Visit(IDivision division) {
        this.Visit(division.LeftOperand);
        this.Visit(division.RightOperand);
        Bpl.Expr rexp = TranslatedExpressions.Pop();
        Bpl.Expr lexp = TranslatedExpressions.Pop();

        var tok = TranslationHelper.CciLocationToBoogieToken(division.Locations);

        var loc = this.StmtTraverser.MetadataTraverser.CreateFreshLocal(division.RightOperand.Type);
        var locExpr = Bpl.Expr.Ident(loc);
        var storeLocal = Bpl.Cmd.SimpleAssign(tok, locExpr, rexp);
        this.StmtTraverser.StmtBuilder.Add(storeLocal);

        var a = new Bpl.AssertCmd(tok, Bpl.Expr.Binary(Bpl.BinaryOperator.Opcode.Neq, locExpr, Bpl.Expr.Literal(0)));
        this.StmtTraverser.StmtBuilder.Add(a);

        TranslatedExpressions.Push(Bpl.Expr.Binary(Bpl.BinaryOperator.Opcode.Div, lexp, locExpr));
      }
    }
  }
}