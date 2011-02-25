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
using System.Diagnostics.Contracts;


namespace BytecodeTranslator {

  /// <summary>
  /// Responsible for traversing all metadata elements (i.e., everything exclusive
  /// of method bodies).
  /// </summary>
  public class MetadataTraverser : BaseMetadataTraverser {

    readonly Sink sink;

    public readonly TraverserFactory factory;

    public readonly PdbReader/*?*/ PdbReader;

    public MetadataTraverser(Sink sink, PdbReader/*?*/ pdbReader)
      : base() {
      this.sink = sink;
      this.factory = sink.Factory;
      this.PdbReader = pdbReader;
    }

    public Bpl.Program TranslatedProgram {
      get { return this.sink.TranslatedProgram; }
    }

    #region Overrides

    public override void Visit(IModule module) {
      base.Visit(module);
    }

    public override void Visit(IAssembly assembly) {
      base.Visit(assembly);
      foreach (ITypeDefinition type in sink.delegateTypeToDelegates.Keys)
      {
        CreateDispatchMethod(type);
      }
    }

    private Bpl.IfCmd BuildBreakCmd(Bpl.Expr b) {
      Bpl.StmtListBuilder ifStmtBuilder = new Bpl.StmtListBuilder();
      ifStmtBuilder.Add(new Bpl.BreakCmd(b.tok, ""));
      return new Bpl.IfCmd(b.tok, b, ifStmtBuilder.Collect(b.tok), null, null);
    }

    private Bpl.IfCmd BuildIfCmd(Bpl.Expr b, Bpl.Cmd cmd, Bpl.IfCmd ifCmd)
    {
      Bpl.StmtListBuilder ifStmtBuilder;
      ifStmtBuilder = new Bpl.StmtListBuilder();
      ifStmtBuilder.Add(cmd);
      return new Bpl.IfCmd(b.tok, b, ifStmtBuilder.Collect(b.tok), ifCmd, null);
    }

    private void CreateDispatchMethod(ITypeDefinition type)
    {
      Contract.Assert(type.IsDelegate);
      IMethodDefinition invokeMethod = null;
      foreach (IMethodDefinition m in type.Methods)
      {
        if (m.Name.Value == "Invoke")
        {
          invokeMethod = m;
          break;
        }
      }
      Bpl.IToken token = invokeMethod.Token();

      Dictionary<IParameterDefinition, MethodParameter> formalMap = new Dictionary<IParameterDefinition, MethodParameter>();
      this.sink.BeginMethod();

      try
      {
        #region Create in- and out-parameters

        int in_count = 0;
        int out_count = 0;
        MethodParameter mp;
        foreach (IParameterDefinition formal in invokeMethod.Parameters)
        {
          mp = new MethodParameter(formal);
          if (mp.inParameterCopy != null) in_count++;
          if (mp.outParameterCopy != null && (formal.IsByReference || formal.IsOut))
            out_count++;
          formalMap.Add(formal, mp);
        }
        this.sink.FormalMap = formalMap;

        #region Look for Returnvalue
        if (invokeMethod.Type.TypeCode != PrimitiveTypeCode.Void)
        {
          Bpl.Type rettype = TranslationHelper.CciTypeToBoogie(invokeMethod.Type);
          out_count++;
          this.sink.RetVariable = new Bpl.Formal(token, new Bpl.TypedIdent(token, "$result", rettype), false);
        }
        else
        {
          this.sink.RetVariable = null;
        }

        #endregion

        in_count++; // for the delegate instance

        Bpl.Variable[] invars = new Bpl.Formal[in_count];
        Bpl.Variable[] outvars = new Bpl.Formal[out_count];

        int i = 0;
        int j = 0;

        invars[i++] = new Bpl.Formal(token, new Bpl.TypedIdent(token, "this", Bpl.Type.Int), true);

        foreach (MethodParameter mparam in formalMap.Values)
        {
          if (mparam.inParameterCopy != null)
          {
            invars[i++] = mparam.inParameterCopy;
          }
          if (mparam.outParameterCopy != null)
          {
            if (mparam.underlyingParameter.IsByReference || mparam.underlyingParameter.IsOut)
              outvars[j++] = mparam.outParameterCopy;
          }
        }

        #region add the returnvalue to out if there is one
        if (this.sink.RetVariable != null) outvars[j] = this.sink.RetVariable;
        #endregion

        #endregion

        string invokeMethodName = TranslationHelper.CreateUniqueMethodName(invokeMethod);
        Bpl.Procedure proc = new Bpl.Procedure(token,
            invokeMethodName, // make it unique!
            new Bpl.TypeVariableSeq(),
            new Bpl.VariableSeq(invars), // in
            new Bpl.VariableSeq(outvars), // out
            new Bpl.RequiresSeq(),
            new Bpl.IdentifierExprSeq(),
            new Bpl.EnsuresSeq());

        this.sink.TranslatedProgram.TopLevelDeclarations.Add(proc);

        Bpl.LocalVariable method = new Bpl.LocalVariable(token, new Bpl.TypedIdent(token, "method", Bpl.Type.Int));
        Bpl.LocalVariable receiver = new Bpl.LocalVariable(token, new Bpl.TypedIdent(token, "receiver", Bpl.Type.Int));
        Bpl.LocalVariable iter = new Bpl.LocalVariable(token, new Bpl.TypedIdent(token, "iter", Bpl.Type.Int));
        Bpl.LocalVariable niter = new Bpl.LocalVariable(token, new Bpl.TypedIdent(token, "niter", Bpl.Type.Int));

        Bpl.StmtListBuilder implStmtBuilder = new Bpl.StmtListBuilder();
        implStmtBuilder.Add(TranslationHelper.BuildAssignCmd(Bpl.Expr.Ident(iter), this.sink.ReadHead(Bpl.Expr.Ident(invars[0]))));

        Bpl.StmtListBuilder whileStmtBuilder = new Bpl.StmtListBuilder();
        whileStmtBuilder.Add(TranslationHelper.BuildAssignCmd(Bpl.Expr.Ident(niter), this.sink.ReadNext(Bpl.Expr.Ident(invars[0]), Bpl.Expr.Ident(iter))));
        whileStmtBuilder.Add(BuildBreakCmd(Bpl.Expr.Eq(Bpl.Expr.Ident(niter), this.sink.ReadHead(Bpl.Expr.Ident(invars[0])))));
        whileStmtBuilder.Add(TranslationHelper.BuildAssignCmd(Bpl.Expr.Ident(method), this.sink.ReadMethod(Bpl.Expr.Ident(invars[0]), Bpl.Expr.Ident(niter))));
        whileStmtBuilder.Add(TranslationHelper.BuildAssignCmd(Bpl.Expr.Ident(receiver), this.sink.ReadReceiver(Bpl.Expr.Ident(invars[0]), Bpl.Expr.Ident(niter))));
        Bpl.IfCmd ifCmd = BuildIfCmd(Bpl.Expr.True, new Bpl.AssumeCmd(token, Bpl.Expr.False), null);
        foreach (IMethodDefinition defn in sink.delegateTypeToDelegates[type])
        {
          Bpl.ExprSeq ins = new Bpl.ExprSeq();
          Bpl.IdentifierExprSeq outs = new Bpl.IdentifierExprSeq();
          if (!defn.IsStatic)
            ins.Add(Bpl.Expr.Ident(receiver));
          int index;
          for (index = 1; index < invars.Length; index++)
          {
            ins.Add(Bpl.Expr.Ident(invars[index]));
          }
          for (index = 0; index < outvars.Length; index++)
          {
            outs.Add(Bpl.Expr.Ident(outvars[index]));
          }
          Bpl.Constant c = sink.FindOrAddDelegateMethodConstant(defn);
          Bpl.Expr bexpr = Bpl.Expr.Binary(Bpl.BinaryOperator.Opcode.Eq, Bpl.Expr.Ident(method), Bpl.Expr.Ident(c)); 
          Bpl.CallCmd callCmd = new Bpl.CallCmd(token, c.Name, ins, outs);
          ifCmd = BuildIfCmd(bexpr, callCmd, ifCmd);
        }
        whileStmtBuilder.Add(ifCmd);
        whileStmtBuilder.Add(TranslationHelper.BuildAssignCmd(Bpl.Expr.Ident(iter), Bpl.Expr.Ident(niter)));
        Bpl.WhileCmd whileCmd = new Bpl.WhileCmd(token, Bpl.Expr.True, new List<Bpl.PredicateCmd>(), whileStmtBuilder.Collect(token));

        implStmtBuilder.Add(whileCmd);

        Bpl.Implementation impl =
            new Bpl.Implementation(token,
                invokeMethodName, // make unique
                new Bpl.TypeVariableSeq(),
                new Bpl.VariableSeq(invars),
                new Bpl.VariableSeq(outvars),
                new Bpl.VariableSeq(iter, niter, method, receiver),
                implStmtBuilder.Collect(token)
                );

        impl.Proc = proc;
        this.sink.TranslatedProgram.TopLevelDeclarations.Add(impl);
      }
      catch (TranslationException te)
      {
        throw new NotImplementedException(te.ToString());
      }
      catch
      {
        throw;
      }
      finally
      {
        // Maybe this is a good place to add the procedure to the toplevel declarations
      }
    }

    /// <summary>
    /// Visits only classes: throws an exception for all other type definitions.
    /// </summary>
    /// 


    public override void Visit(ITypeDefinition typeDefinition) {

      if (typeDefinition.IsClass) {
        sink.FindOrCreateType(typeDefinition);
        base.Visit(typeDefinition);
      } else if (typeDefinition.IsDelegate) {
        sink.AddDelegateType(typeDefinition);
      } else {
        Console.WriteLine("Non-Class {0} was found", typeDefinition);
        throw new NotImplementedException(String.Format("Non-Class Type {0} is not yet supported.", typeDefinition.ToString()));
      }
    }

    #region Local state for each method

    #endregion

    /// <summary>
    /// 
    /// </summary>
    public override void Visit(IMethodDefinition method) {
      bool isEventAddOrRemove = method.IsSpecialName && (method.Name.Value.StartsWith("add_") || method.Name.Value.StartsWith("remove_"));
      if (isEventAddOrRemove)
        return;

      this.sink.BeginMethod();

      var proc = this.sink.FindOrCreateProcedure(method, method.IsStatic);

      try {

        if (method.IsAbstract) {
          throw new NotImplementedException("abstract methods are not yet implemented");
        }

        StatementTraverser stmtTraverser = this.factory.MakeStatementTraverser(this.sink, this.PdbReader);

        #region Add assignements from In-Params to local-Params

        foreach (MethodParameter mparam in this.sink.FormalMap.Values) {
          if (mparam.inParameterCopy != null) {
            Bpl.IToken tok = method.Token();
            stmtTraverser.StmtBuilder.Add(Bpl.Cmd.SimpleAssign(tok,
              new Bpl.IdentifierExpr(tok, mparam.outParameterCopy),
              new Bpl.IdentifierExpr(tok, mparam.inParameterCopy)));
          }
        }

        #endregion

        try {
          method.Body.Dispatch(stmtTraverser);
        } catch (TranslationException te) {
          throw new NotImplementedException("No Errorhandling in Methodvisitor / " + te.ToString());
        } catch {
          throw;
        }

        #region Create Local Vars For Implementation
        List<Bpl.Variable> vars = new List<Bpl.Variable>();
        foreach (MethodParameter mparam in this.sink.FormalMap.Values) {
          if (!(mparam.underlyingParameter.IsByReference || mparam.underlyingParameter.IsOut))
            vars.Add(mparam.outParameterCopy);
        }
        foreach (Bpl.Variable v in this.sink.LocalVarMap.Values) {
          vars.Add(v);
        }

        Bpl.VariableSeq vseq = new Bpl.VariableSeq(vars.ToArray());
        #endregion

        Bpl.Implementation impl =
            new Bpl.Implementation(method.Token(),
                proc.Name,
                new Microsoft.Boogie.TypeVariableSeq(),
                proc.InParams,
                proc.OutParams,
                vseq,
                stmtTraverser.StmtBuilder.Collect(Bpl.Token.NoToken));

        impl.Proc = proc;

        // Don't need an expression translator because there is a limited set of things
        // that can appear as arguments to custom attributes
        foreach (var a in method.Attributes) {
          var attrName = TypeHelper.GetTypeName(a.Type);
          if (attrName.EndsWith("Attribute"))
            attrName = attrName.Substring(0, attrName.Length - 9);
          var args = new object[IteratorHelper.EnumerableCount(a.Arguments)];
          int argIndex = 0;
          foreach (var c in a.Arguments) {
            var mdc = c as IMetadataConstant;
            if (mdc != null) {
              object o;
              switch (mdc.Type.TypeCode) {
                case PrimitiveTypeCode.Boolean:
                  o = (bool)mdc.Value ? Bpl.Expr.True : Bpl.Expr.False;
                  break;
                case PrimitiveTypeCode.Int32:
                  o = Bpl.Expr.Literal((int)mdc.Value);
                  break;
                case PrimitiveTypeCode.String:
                  o = mdc.Value;
                  break;
                default:
                  throw new InvalidCastException("Invalid metadata constant type");
              }
              args[argIndex++] = o;
            }
          }
          impl.AddAttribute(attrName, args);
        }

        this.sink.TranslatedProgram.TopLevelDeclarations.Add(impl);

      } catch (TranslationException te) {
        throw new NotImplementedException(te.ToString());
      } catch {
        throw;
      } finally {
        // Maybe this is a good place to add the procedure to the toplevel declarations
      }
    }

    public override void Visit(IFieldDefinition fieldDefinition) {
      this.sink.FindOrCreateFieldVariable(fieldDefinition);
    }

    #endregion

  }
}